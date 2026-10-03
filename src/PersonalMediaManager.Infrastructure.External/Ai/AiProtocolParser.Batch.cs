using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
namespace PersonalMediaManager.Infrastructure.External.Ai;

internal sealed partial class AiProtocolParser
{
    private sealed record BatchInput(AiProviderType Protocol, AiProviderEndpoint Endpoint, AiParseRequest Request,
        AiTransportScope? Transport, Action<string, object?> Emit, Func<IDisposable> Activate, AiBatchOptions Options);
    private sealed record BatchResult(AiParseOutcome? Outcome, Exception? Error = null);
    private sealed record Prepared(AiBatchEntry<BatchInput> Entry, string System, string User,
        AiPromptHelpers.PreparedTaskPrompt? Task, int OutputTokens);
    private static Prepared Prepare(AiBatchEntry<BatchInput> item)
    {
        AiParseRequest request = item.Input.Request;
        AiPromptHelpers.PreparedTaskPrompt? task = request.Context is null ? null : AiPromptHelpers.PrepareTaskPrompt(request);
        return new(item, task is null ? AiPromptHelpers.SystemPrompt : AiPromptHelpers.GetTaskSystemPrompt(task.Request),
            task?.UserPrompt ?? AiPromptHelpers.BuildUserPrompt(request), task,
            (request.Context is { SchemaVersion: 2, OutputDetail: AiOutputDetail.Compact } ? 512 : 1024)
                + Math.Min(task?.Metadata.CandidateCount ?? 0, 5) * 32);
    }
    private static string CompatibilityKey(BatchInput input)
    {
        // 完整端点只参与内存分组，不记录此哈希、端点或密钥。
        Prepared prepared = Prepare(new("", input, default));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { input.Protocol, input.Endpoint, input.Transport?.ProviderId, input.Options, prepared.System,
            input.Request.Context?.SchemaVersion, input.Request.Context?.TaskType, input.Request.Context?.OutputDetail }))));
    }
    private async Task<IReadOnlyDictionary<string, BatchResult>> ExecuteBatchAsync(
        IReadOnlyList<AiBatchEntry<BatchInput>> entries, CancellationToken ct)
    {
        AiBatchOptions options = entries[0].Input.Options;
        Dictionary<string, BatchResult> results = new(StringComparer.Ordinal);
        List<Prepared> chunk = [];
        int bytes = 0, output = 0, response = 0, physicalRequests = 0, automaticFallbacks = 0;
        foreach (AiBatchEntry<BatchInput> entry in entries)
        {
            if (entry.CancellationToken.IsCancellationRequested)
            { results[entry.Id] = new(null, new OperationCanceledException(entry.CancellationToken)); continue; }
            Prepared item = Prepare(entry);
            int expectedResponseBytes = checked(item.OutputTokens * 12 + 128);
            int overhead = AiBatchJson.TokenUpperBound(item.System + AiBatchJson.Instruction) + 256;
            int added = AiBatchJson.TokenUpperBound(JsonSerializer.Serialize(new { id = entry.Id, input = item.User }));
            if (chunk.Count > 0 && (chunk.Count >= options.ExternalMaxItems
                || overhead + bytes + added + output + item.OutputTokens > options.ContextTokenBudget
                || output + item.OutputTokens > options.MaxOutputTokens
                || response + expectedResponseBytes > options.MaxResponseBytes))
            {
                entry.Input.Emit("ai.batch_split", new { Reason = "configured_budget_or_size", PlannedItems = chunk.Count,
                    InputUpperBound = bytes, OutputBudget = output, ExpectedResponseBytes = response,
                    options.ExternalMaxItems, options.ContextTokenBudget, options.MaxOutputTokens, options.MaxResponseBytes });
                await SendChunkAsync(chunk); chunk.Clear(); bytes = 0; output = 0; response = 0;
            }
            chunk.Add(item); bytes += added; output += item.OutputTokens; response += expectedResponseBytes;
        }
        if (chunk.Count > 0) await SendChunkAsync(chunk);
        return results;

        async Task SingleAsync(Prepared item, string? batchId = null)
        {
            using IDisposable activated = item.Entry.Input.Activate();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, item.Entry.CancellationToken);
            try
            {
                results[item.Entry.Id] = new(await ParseSingleAsync(item.Entry.Input.Protocol, item.Entry.Input.Endpoint,
                    item.Entry.Input.Request, item.Entry.Input.Transport, linked.Token, () => physicalRequests++, batchId, item.Entry.Id, options));
                item.Entry.ResponseReady?.Invoke();
            }
            catch (Exception ex) { results[item.Entry.Id] = new(null, ex); }
        }
        async Task SendChunkAsync(IReadOnlyList<Prepared> items)
        {
            ct.ThrowIfCancellationRequested();
            Prepared[] active = items.Where(i => !i.Entry.CancellationToken.IsCancellationRequested).ToArray();
            foreach (Prepared cancelled in items.Except(active))
                results[cancelled.Entry.Id] = new(null, new OperationCanceledException(cancelled.Entry.CancellationToken));
            if (active.Length == 0) return;
            if (active.Length == 1) { await SingleAsync(active[0]); return; }
            int requestsBefore = physicalRequests;
            Prepared first = active[0];
            BatchInput input = first.Entry.Input;
            if (!_protocols.TryGetValue(input.Protocol, out IAiProtocol? protocol))
            { foreach (Prepared item in active) results[item.Entry.Id] = new(null, new AiProviderLogicalException("AI 协议不可用")); return; }
            string batchId = Guid.NewGuid().ToString("N");
            string user = JsonSerializer.Serialize(new { items = active.Select(i => new { id = i.Entry.Id, input = i.User }) });
            string system = first.System + AiBatchJson.Instruction;
            int maxTokens = active.Sum(i => i.OutputTokens);
            // 最终序列化后重新检查，不能遗漏 JSON 转义与封装。
            if (AiBatchJson.TokenUpperBound(system) + AiBatchJson.TokenUpperBound(user) + maxTokens > options.ContextTokenBudget)
            {
                input.Emit("ai.batch_split", new { Reason = "serialized_input_budget", ItemCount = active.Length,
                    options.ContextTokenBudget, RequestedOutputTokens = maxTokens });
                int middle = active.Length / 2;
                await SendChunkAsync(active[..middle]);
                await SendChunkAsync(active[middle..]);
                return;
            }
            using IDisposable activated = input.Activate();
            string requestId = Guid.NewGuid().ToString("N");
            using IDisposable physicalDiagnostic = ParseDiagnostics.BeginAiCall(requestId, batchId, credential: input.Endpoint.ApiKey);
            using IDisposable broadcast = ParseDiagnostics.BeginBroadcast(active.Select(i => i.Entry.Input.Emit).ToArray());
            if (ParseDiagnostics.IsFull)
            {
                ParseDiagnostics.Emit("ai.batch_request", new { stage = "prepared_not_yet_sent", batchId,
                    itemIds = active.Select(i => i.Entry.Id).ToArray(),
                    system = ParseDiagnostics.CaptureText(system), user = ParseDiagnostics.CaptureText(user), maxTokens });
                foreach (Prepared member in active)
                {
                    using IDisposable memberContext = member.Entry.Input.Activate();
                    using IDisposable memberCall = ParseDiagnostics.BeginAiCall(requestId, batchId, member.Entry.Id, credential: input.Endpoint.ApiKey);
                    ParseDiagnostics.Emit("ai.input", new { stage = "original_request_context", content = ParseDiagnostics.CaptureText(JsonSerializer.Serialize(member.Entry.Input.Request)) });
                }
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            AiCompletion completion;
            int? usageIndex = null;
            async Task BeforeSend(CancellationToken sendToken)
            {
                usageIndex = input.Transport is null ? null : await input.Transport.StartedAsync(sendToken);
                physicalRequests++;
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                if (protocol is not IAiSendBoundaryProtocol) await BeforeSend(ct);
                completion = await protocol.CompleteAsync(new(input.Endpoint,
                    [new("system", system), new("user", user)], input.Endpoint.StructuredJson, 0, maxTokens,
                    MaxResponseBytes: options.MaxResponseBytes, BeforeSend: BeforeSend, DisableThinking: options.DisableThinking), ct);
                if (usageIndex is int recorded) await input.Transport!.CompletedAsync(recorded, completion.PromptTokens, completion.CompletionTokens);
            }
            catch (Exception ex)
            {
                // 网络和限流失败不扩散为 N 次即时回退。
                foreach (Prepared item in active) results[item.Entry.Id] = new(null, CopyFailure(ex));
                ParseDiagnostics.Emit("ai.batch_failed", new { BatchId = batchId, ItemIds = active.Select(i => i.Entry.Id).ToArray(),
                    ExceptionType = ex.GetType().Name, ElapsedMs = elapsed.ElapsedMilliseconds,
                    httpStatus = ex.Data[AiCallDiagnostics.HttpStatusKey] as int?,
                    response = ParseDiagnostics.IsFull && ex.Data[AiCallDiagnostics.ResponseTextKey] is string failedBody ? ParseDiagnostics.CaptureText(failedBody) : ParseDiagnostics.UnknownText() });
                return;
            }
            finally
            {
                if (usageIndex is int recorded) await input.Transport!.SettleAsync(recorded);
            }
            if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.batch_response", new { content = ParseDiagnostics.CaptureText(completion.Text) with { Boundary = "redacted_extracted_assistant_content" }, completion.PromptTokens, completion.CompletionTokens });
            IReadOnlyDictionary<string, string> mapped;
            try
            {
                if (completion.IsTruncated) throw new FormatException("AI 批量输出已被供应商截断");
                mapped = AiBatchJson.Parse(completion.Text, active.Select(i => i.Entry.Id).ToArray(), options.MaxResponseBytes);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.cleanup", new { stage = "batch_envelope_rejected", reason = ex.GetType().Name, error = ParseDiagnostics.CaptureText(ex.Message) });
                mapped = new Dictionary<string, string>();
            }
            List<Prepared> fallbacks = [];
            foreach (Prepared item in active)
            {
                using IDisposable memberContext = item.Entry.Input.Activate();
                using IDisposable memberCall = ParseDiagnostics.BeginAiCall(requestId, batchId, item.Entry.Id, credential: input.Endpoint.ApiKey);
                if (item.Entry.CancellationToken.IsCancellationRequested)
                { results[item.Entry.Id] = new(null, new OperationCanceledException(item.Entry.CancellationToken)); continue; }
                if (mapped.TryGetValue(item.Entry.Id, out string? text))
                {
                    try
                    {
                        if (Encoding.UTF8.GetByteCount(text) > item.OutputTokens * 12)
                            throw new AiProviderLogicalException("AI 批量单项响应超过预留大小");
                        if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.cleanup", new { stage = "batch_item_json_extraction", content = ParseDiagnostics.CaptureText(text) });
                        AiParseResult parsed = item.Task is null
                            ? AiPromptHelpers.GroundYear(AiPromptHelpers.ParseContent(text), item.Entry.Input.Request)
                            : AiPromptHelpers.ParseTaskContent(text, item.Task.Request);
                        if (BorrowedTitle(parsed.Title, item, active)
                            || parsed.Validation?.SchemaIssues?.Any(issue => issue.BlocksAcceptance) == true)
                            throw new AiProviderLogicalException("批量单项结构或跨项证据校验失败");
                        // 只保存该项正文，不能把兄弟项整包放进单项审计。
                        string? Sanitize(string? value) => string.IsNullOrEmpty(input.Endpoint.ApiKey) ? value
                            : value?.Replace(input.Endpoint.ApiKey, "[凭据已脱敏]", StringComparison.Ordinal);
                        results[item.Entry.Id] = new(new(parsed, Sanitize(item.User), Sanitize(text),
                            item == first ? completion.PromptTokens : null, item == first ? completion.CompletionTokens : null, item.Task?.Metadata));
                        if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.parse_result", new { parsed = true, structured = ParseDiagnostics.CaptureText(JsonSerializer.Serialize(parsed)), validation = parsed.Validation });
                        item.Entry.ResponseReady?.Invoke();
                        continue;
                    }
                    catch (AiProviderLogicalException ex)
                    { if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.cleanup", new { stage = "batch_item_rejected", reason = ParseDiagnostics.CaptureText(ex.Message), fallback = "single_once" }); }
                }
                if (!mapped.ContainsKey(item.Entry.Id) && ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.cleanup", new { stage = "batch_item_missing", fallback = "single_once" });
                fallbacks.Add(item);
            }
            // 先保存并标记同包全部合法结果，再串行回退坏项，避免兄弟项被回退等待误超时。
            foreach (Prepared item in fallbacks)
            {
                if (automaticFallbacks >= AiBatchOptions.MaxAutomaticFallbackRequests
                    || item.Entry.TryTakeFallback is not null && !item.Entry.TryTakeFallback())
                {
                    results[item.Entry.Id] = new(null, new AiProviderBatchDeferredException("本批自动回退预算已用尽，该项未继续调用 AI，请核对后重试"));
                    item.Entry.Input.Emit("ai.batch_fallback_deferred", new { BatchId = batchId, ItemId = item.Entry.Id,
                        AutomaticFallbacks = automaticFallbacks, Limit = AiBatchOptions.MaxAutomaticFallbackRequests });
                    item.Entry.TerminalReady?.Invoke();
                    continue;
                }
                if (item.Entry.CancellationToken.IsCancellationRequested)
                { results[item.Entry.Id] = new(null, new OperationCanceledException(item.Entry.CancellationToken)); continue; }
                automaticFallbacks++;
                await SingleAsync(item, batchId);
            }
            ParseDiagnostics.Emit("ai.batch_completed", new { BatchId = batchId, ItemIds = active.Select(i => i.Entry.Id).ToArray(),
                ItemCount = active.Length, PhysicalRequests = physicalRequests - requestsBefore, FallbackItems = fallbacks.Count,
                ManagedMemoryBytesAfterBatch = GC.GetTotalMemory(false),
                ElapsedMs = elapsed.ElapsedMilliseconds, completion.PromptTokens, completion.CompletionTokens,
                EstimatedInputTokenUpperBound = AiBatchJson.TokenUpperBound(system) + AiBatchJson.TokenUpperBound(user),
                MaxOutputTokens = maxTokens, ResponseBytes = Encoding.UTF8.GetByteCount(completion.Text) });
        }
    }
    private static bool BorrowedTitle(string title, Prepared own, IReadOnlyList<Prepared> batch)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        bool Contains(AiParseRequest request) => new[] { request.FileName, request.ParentFolderName, request.RuleHintTitle }
            .Concat(request.RelativeSegments ?? []).Any(source => source?.Contains(title, StringComparison.OrdinalIgnoreCase) == true);
        return !Contains(own.Entry.Input.Request) && batch.Any(other => other != own && Contains(other.Entry.Input.Request));
    }
    private static Exception CopyFailure(Exception exception)
    {
        Exception copy = exception switch
        {
        AiProviderRateLimitException => new AiProviderRateLimitException("AI 批量请求受到限流"),
        AiProviderTransientException => new AiProviderTransientException("AI 批量请求瞬时失败"),
        AiProviderLogicalException logical => new AiProviderLogicalException("AI 批量请求失败", logical.HttpStatus),
        AiProviderModelRuntimeException => new AiProviderModelRuntimeException("AI 批量模型运行失败"),
        OperationCanceledException => new OperationCanceledException("AI 批量请求已取消"),
            _ => new AiProviderLogicalException("AI 批量请求失败"),
        };
        if (exception.Data[AiCallDiagnostics.HttpStatusKey] is int status)
            copy.Data[AiCallDiagnostics.HttpStatusKey] = status;
        else if (exception is AiProviderLogicalException { HttpStatus: int code })
            copy.Data[AiCallDiagnostics.HttpStatusKey] = code;
        return copy;
    }
}
