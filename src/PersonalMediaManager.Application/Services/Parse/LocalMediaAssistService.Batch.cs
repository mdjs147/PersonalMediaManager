using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Dtos.LocalAi;
namespace PersonalMediaManager.Application.Services.Parse;

public sealed partial class LocalMediaAssistService
{
    private sealed record LocalBatchInput(LocalAiInferenceRequest Request, LocalAiSettingsDto Settings,
        Action<string, object?> Emit, Func<IDisposable> Activate, AiBatchOptions Options);
    private async Task<IReadOnlyDictionary<string, LocalAiInferenceResult>> GenerateBatchAsync(
        IReadOnlyList<AiBatchEntry<LocalBatchInput>> entries, CancellationToken ct)
    {
        AiBatchOptions options = entries[0].Input.Options;
        options.Validate();
        Dictionary<string, LocalAiInferenceResult> results = new(StringComparer.Ordinal);
        int physicalRequests = 0;
        foreach (AiBatchEntry<LocalBatchInput>[] chunk in entries.Chunk(options.LocalMaxItems))
        {
            AiBatchEntry<LocalBatchInput>[] active = chunk.Where(e => !e.CancellationToken.IsCancellationRequested).ToArray();
            foreach (AiBatchEntry<LocalBatchInput> cancelled in chunk.Except(active)) results[cancelled.Id] = new(null, "cancelled");
            if (active.Length == 0) continue;
            LocalAiSettingsDto configuration = active[0].Input.Settings;
            string system = LocalTitleSpanProtocol.SystemPrompt + AiBatchJson.Instruction;
            string user = JsonSerializer.Serialize(new { items = active.Select(e => new
                { id = e.Id, input = JsonSerializer.Deserialize<JsonElement>(e.Input.Request.UserPrompt) }) },
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            int maxOutput = 96 * active.Length;
            int estimated = AiBatchJson.TokenUpperBound(system) + AiBatchJson.TokenUpperBound(user);
            // 沿用原 context，绝不为了凑批扩大 KV cache。
            if (active.Length == 1 || estimated + maxOutput > configuration.ContextTokens
                || maxOutput > configuration.MaxOutputTokens)
            { foreach (AiBatchEntry<LocalBatchInput> item in active) await SingleAsync(item); continue; }
            int requestsBefore = physicalRequests;
            string batchId = Guid.NewGuid().ToString("N");
            using IDisposable activated = active[0].Input.Activate();
            string requestId = Guid.NewGuid().ToString("N");
            using IDisposable physicalCall = ParseDiagnostics.BeginAiCall(requestId, batchId);
            using IDisposable broadcast = ParseDiagnostics.BeginBroadcast(active.Select(i => i.Input.Emit).ToArray());
            Stopwatch elapsed = Stopwatch.StartNew();
            LocalAiInferenceResult inference = await client.GenerateAsync(new(system, user, maxOutput,
                BatchSpanCounts: active.ToDictionary(e => e.Id, e => e.Input.Request.AllowedSpanCount ?? 0)), ct).WaitAsync(ct);
            if (inference.Attempted) physicalRequests++;
            if (!inference.Success || inference.ModelId is not null && inference.ModelId != configuration.ModelId)
            {
                foreach (AiBatchEntry<LocalBatchInput> item in active)
                    results[item.Id] = inference with { Content = null, FailureReason = inference.FailureReason ?? "model_changed" };
                continue;
            }
            if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("local_ai.batch_response", new { content = ParseDiagnostics.CaptureText(inference.Content) });
            IReadOnlyDictionary<string, string> mapped;
            try { mapped = AiBatchJson.Parse(inference.Content!, active.Select(e => e.Id).ToArray(), options.MaxResponseBytes); }
            catch (Exception ex) when (ex is JsonException or FormatException) { mapped = new Dictionary<string, string>(); }
            List<AiBatchEntry<LocalBatchInput>> fallbacks = [];
            foreach (AiBatchEntry<LocalBatchInput> item in active)
            {
                using IDisposable memberContext = item.Input.Activate();
                using IDisposable memberCall = ParseDiagnostics.BeginAiCall(requestId, batchId, item.Id);
                if (item.CancellationToken.IsCancellationRequested) { results[item.Id] = new(null, "cancelled"); continue; }
                if (mapped.TryGetValue(item.Id, out string? text) && ValidIndex(text, item.Input.Request.AllowedSpanCount ?? 0))
                {
                    if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("local_ai.cleanup", new { stage = "closed_index_accepted", content = ParseDiagnostics.CaptureText(text) });
                    results[item.Id] = inference with { Content = text };
                    item.ResponseReady?.Invoke();
                }
                else
                {
                    if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("local_ai.cleanup", new { stage = "closed_index_rejected_or_missing", fallback = "single_once", content = ParseDiagnostics.CaptureText(text) });
                    fallbacks.Add(item);
                }
            }
            // 合法兄弟项先停自己的推理计时，再执行坏项回退。
            foreach (AiBatchEntry<LocalBatchInput> item in fallbacks)
            {
                if (item.TryTakeFallback is not null && !item.TryTakeFallback())
                {
                    results[item.Id] = inference with { Content = null, FailureReason = "batch_retry_deferred" };
                    item.Input.Emit("local_ai.batch_fallback_deferred", new { BatchId = batchId, ItemId = item.Id,
                        Limit = AiBatchOptions.MaxAutomaticFallbackRequests });
                    item.TerminalReady?.Invoke();
                    continue;
                }
                await SingleAsync(item, batchId);
            }
            ParseDiagnostics.Emit("local_ai.batch_completed", new { BatchId = batchId,
                ItemIds = active.Select(e => e.Id).ToArray(), ItemCount = active.Length,
                PhysicalRequests = physicalRequests - requestsBefore, FallbackItems = fallbacks.Count, EstimatedInputTokenUpperBound = estimated,
                inference.PromptTokens, inference.CompletionTokens, inference.WorkingSetBytes,
                ManagedMemoryBytesAfterBatch = GC.GetTotalMemory(false),
                MaxOutputTokens = maxOutput, configuration.ContextTokens, ElapsedMs = elapsed.ElapsedMilliseconds });
        }
        return results;
        async Task SingleAsync(AiBatchEntry<LocalBatchInput> item, string? batchId = null)
        {
            using IDisposable activated = item.Input.Activate();
            using IDisposable physicalCall = ParseDiagnostics.BeginAiCall(Guid.NewGuid().ToString("N"), batchId, item.Id);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, item.CancellationToken);
            try
            {
                LocalAiInferenceResult result = await client.GenerateAsync(item.Input.Request, linked.Token).WaitAsync(linked.Token);
                results[item.Id] = result;
                if (result.Attempted) physicalRequests++;
                if (result.Success) item.ResponseReady?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { results[item.Id] = new(null, "cancelled"); }
            catch (Exception) { results[item.Id] = new(null, "inference_unavailable"); }
        }
    }
    private static bool ValidIndex(string text, int count)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonProperty[] fields = doc.RootElement.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "index") return false;
        JsonElement value = fields[0].Value;
        return value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int index) && index >= 0 && index < count;
    }
}
