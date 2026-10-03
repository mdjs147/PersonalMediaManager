using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PersonalMediaManager.Application.Common.Diagnostics;

/// <summary>解析诊断详细级别</summary>
public enum ParseDiagnosticLevel { Off, Standard, Detailed, Full }

/// <summary>本地诊断容量与隐私选项</summary>
public sealed class ParseDiagnosticOptions
{
    public ParseDiagnosticLevel Level { get; set; } = ParseDiagnosticLevel.Standard;
    public int MaxTextUtf8Bytes { get; set; } = 16_384;
    public int MaxEventUtf8Bytes { get; set; } = 65_536;
    public long MaxFileBytes { get; set; } = 4 * 1024 * 1024;
    public long MaxTotalBytes { get; set; } = 32 * 1024 * 1024;
    public int RetentionDays { get; set; } = 7;
    public int MaxFiles { get; set; } = 16;
    public int MaxArtifactUtf8Bytes { get; set; } = 2 * 1024 * 1024;
    public long MaxArtifactTotalBytes { get; set; } = 64 * 1024 * 1024;
    public int MaxArtifacts { get; set; } = 256;

    /// <summary>钳制配置以保持有界开销</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Level)) Level = ParseDiagnosticLevel.Standard;
        MaxTextUtf8Bytes = Math.Clamp(MaxTextUtf8Bytes, 256, 65_536);
        MaxEventUtf8Bytes = Math.Clamp(MaxEventUtf8Bytes, 2048, 262_144);
        MaxFileBytes = Math.Clamp(MaxFileBytes, MaxEventUtf8Bytes + 1L, 64 * 1024 * 1024);
        MaxTotalBytes = Math.Clamp(MaxTotalBytes, MaxFileBytes, 512 * 1024 * 1024);
        RetentionDays = Math.Clamp(RetentionDays, 1, 90);
        MaxFiles = Math.Clamp(MaxFiles, 1, 128);
        MaxArtifactUtf8Bytes = Math.Clamp(MaxArtifactUtf8Bytes, 1024, 8 * 1024 * 1024);
        MaxArtifactTotalBytes = Math.Clamp(MaxArtifactTotalBytes, MaxArtifactUtf8Bytes, 256 * 1024 * 1024);
        MaxArtifacts = Math.Clamp(MaxArtifacts, 1, 2048);
    }
}

/// <summary>正文捕获状态与明确的完整性信息</summary>
/// <remarks>哈希针对脱敏后的完整内容，绝不为密钥或私有推理生成指纹。missing=已观察为空；unknown=未能观察；not_recorded=策略不保存。UTF-8 长度仅针对可保存的脱敏内容。</remarks>
public sealed record DiagnosticText(string State, string? Text, string? Sha256, int? OriginalUtf8Bytes,
    int CapturedUtf8Bytes, bool Truncated, bool Redacted, string? ArtifactId = null,
    string? Reason = null, bool FormatPreserved = true, string Boundary = "redacted_text", int? ObservedUtf8Bytes = null);

/// <summary>可离线关联的结构化事件</summary>
public sealed record ParseDiagnosticEvent(int SchemaVersion, DateTimeOffset Timestamp, string RunId,
    string? ScanRunId, long? MediaItemId, long Sequence, string Operation, string Name, JsonElement Data, bool DataRedacted = false,
    string? RequestId = null, string? BatchId = null, string? ItemId = null, int? Attempt = null);

/// <summary>本地诊断存储契约</summary>
public interface IParseDiagnosticArtifactSink
{
    DiagnosticText StoreArtifact(DiagnosticText metadata, string redactedText);
}

public interface IParseDiagnosticSink
{
    ParseDiagnosticOptions Options { get; }
    void Write(ParseDiagnosticEvent value);
}

/// <summary>随异步调用流传递的解析诊断上下文</summary>
/// <remarks>静态门面不持有进程级接收器；每个 scope 自带接收器，避免多宿主和测试串线。写入异常只增加失败计数，不改变业务结果。</remarks>
public static class ParseDiagnostics
{
    private static readonly AsyncLocal<Context?> Ambient = new();
    private static readonly AsyncLocal<AiCorrelation?> AiAmbient = new();
    private static readonly AsyncLocal<IReadOnlyList<Action<string, object?>>?> Broadcast = new();
    private static readonly AsyncLocal<int> AttemptAmbient = new();
    public static IDisposable BeginAttempt(int attempt)
    {
        int previous = AttemptAmbient.Value; AttemptAmbient.Value = attempt;
        return new Restore(() => AttemptAmbient.Value = previous);
    }
    public static IDisposable BeginBroadcast(IReadOnlyList<Action<string, object?>> emitters)
    {
        IReadOnlyList<Action<string, object?>>? previous = Broadcast.Value; Broadcast.Value = emitters;
        return new Restore(() => Broadcast.Value = previous);
    }
    private sealed record AiCorrelation(string RequestId, string? BatchId, string? ItemId, int Attempt, string? Credential);
    public static bool IsFull => Level == ParseDiagnosticLevel.Full;
    public static string? CurrentRequestId => AiAmbient.Value?.RequestId;

    public static Func<IDisposable> CaptureActivation()
    {
        Context? context = Ambient.Value;
        AiCorrelation? correlation = AiAmbient.Value;
        int attempt = AttemptAmbient.Value;
        return () => { Context? old = Ambient.Value; AiCorrelation? oldAi = AiAmbient.Value;
            int oldAttempt = AttemptAmbient.Value;
            IReadOnlyList<Action<string, object?>>? oldBroadcast = Broadcast.Value;
            Ambient.Value = context; AiAmbient.Value = correlation; AttemptAmbient.Value = attempt; Broadcast.Value = null;
            return new Restore(() => { Ambient.Value = old; AiAmbient.Value = oldAi; AttemptAmbient.Value = oldAttempt; Broadcast.Value = oldBroadcast; }); };
    }

    public static IDisposable BeginAiCall(string requestId, string? batchId = null, string? itemId = null, int? attempt = null, string? credential = null)
    {
        AiCorrelation? previous = AiAmbient.Value;
        AiAmbient.Value = new(requestId, batchId, itemId, attempt ?? Math.Max(1, AttemptAmbient.Value), credential);
        return new Restore(() => AiAmbient.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
    private static readonly ParseDiagnosticOptions DefaultOptions = new();
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string? CurrentRunId => Ambient.Value?.RunId;
    public static string? CurrentScanRunId => Ambient.Value?.ScanRunId;
    public static ParseDiagnosticLevel Level => (Ambient.Value?.Sink?.Options ?? DefaultOptions).Level;

    public static IDisposable Begin(string operation, string? runId = null, long? mediaItemId = null,
        IParseDiagnosticSink? sink = null, string? scanRunId = null)
    {
        Context? previous = Ambient.Value;
        string id = runId ?? Guid.NewGuid().ToString("N");
        Context context = new(operation, id, operation == "scan" ? id : scanRunId ?? previous?.ScanRunId,
            mediaItemId ?? previous?.MediaItemId, sink ?? previous?.Sink, previous);
        Ambient.Value = context;
        Emit("operation.started", new { level = Level.ToString(), sourceVersion = typeof(ParseDiagnostics).Assembly.GetName().Version?.ToString(), sourceModuleId = typeof(ParseDiagnostics).Module.ModuleVersionId });
        return context;
    }

    public static void SetMediaItem(long id)
    {
        if (Ambient.Value is { } context) context.MediaItemId = id;
    }

    /// <summary>捕获当前项诊断关联，不借用调度器的环境</summary>
    public static Action<string, object?> CaptureEmitter()
    {
        Context? captured = Ambient.Value;
        // 逻辑重试归属项自身；共享请求的 requestId/batchId 仍来自发送时的物理上下文。
        int? attempt = AttemptAmbient.Value > 0 ? AttemptAmbient.Value : AiAmbient.Value?.Attempt;
        return (eventName, data) => EmitTo(captured, eventName, data, attempt);
    }

    public static void Emit(string eventName, object? data = null)
    {
        if (Broadcast.Value is { } emitters) { foreach (Action<string, object?> emit in emitters) emit(eventName, data); }
        else EmitTo(Ambient.Value, eventName, data);
    }

    private static void EmitTo(Context? context, string eventName, object? data, int? capturedAttempt = null)
    {
        if (context?.Sink is null || context.Sink.Options.Level == ParseDiagnosticLevel.Off) return;
        try
        {
            JsonElement original = JsonSerializer.SerializeToElement(data, JsonOptions);
            JsonElement safe = DiagnosticPrivacy.Sanitize(original);
            context.Sink.Write(new(2, DateTimeOffset.UtcNow, context.RunId, context.ScanRunId,
                context.MediaItemId, Interlocked.Increment(ref context.Sequence), context.Operation, eventName, safe, original.GetRawText() != safe.GetRawText(),
                AiAmbient.Value?.RequestId, AiAmbient.Value?.BatchId, AiAmbient.Value?.ItemId, capturedAttempt ?? AiAmbient.Value?.Attempt));
        }
        catch (Exception) { Interlocked.Increment(ref context.WriteFailures); }
    }

    /// <summary>捕获可公开的输入与输出，标记策略缺省和截断</summary>
    public static DiagnosticText CaptureText(string? text, bool includeAtStandard = false, int? maxUtf8Bytes = null, string? credential = null)
    {
        if (text is null) return new("missing", null, null, null, 0, false, false);
        ParseDiagnosticOptions options = Ambient.Value?.Sink?.Options ?? DefaultOptions;
        credential ??= AiAmbient.Value?.Credential;
        if (options.Level == ParseDiagnosticLevel.Full)
        {
            try
            {
                int observedBytes = Encoding.UTF8.GetByteCount(text);
                // 大响应先按原始大小拒绝捕获，避免为了保存失败的 artifact 执行昂贵过滤或保留前缀。
                if (observedBytes > options.MaxArtifactUtf8Bytes)
                    return new("not_recorded", null, null, null, 0, true, false, Reason: "artifact_size_limit",
                        FormatPreserved: false, Boundary: "uncaptured_input_size_boundary", ObservedUtf8Bytes: observedBytes);
                RawDiagnosticRedaction redaction = DiagnosticRawPrivacy.Redact(text, credential);
                byte[] rawBytes = Encoding.UTF8.GetBytes(redaction.Text);
                DiagnosticText metadata = new(redaction.State, null, Convert.ToHexStringLower(SHA256.HashData(rawBytes)), rawBytes.Length,
                    0, false, redaction.Redacted, Reason: redaction.State == "recorded" ? null : redaction.State,
                    FormatPreserved: redaction.FormatPreserved, Boundary: "redacted_original_format_utf8", ObservedUtf8Bytes: Encoding.UTF8.GetByteCount(text));
                if (redaction.State != "recorded") return metadata with { State = "not_recorded" };
                if (rawBytes.Length > options.MaxArtifactUtf8Bytes)
                    return metadata with { State = "not_recorded", Reason = "artifact_size_limit", Truncated = true };
                return Ambient.Value?.Sink is IParseDiagnosticArtifactSink artifactSink
                    ? artifactSink.StoreArtifact(metadata, redaction.Text)
                    : metadata with { State = "not_recorded", Reason = "artifact_sink_unavailable" };
            }
            catch (Exception) { return new("not_recorded", null, null, null, 0, false, true, Reason: "artifact_capture_failed", FormatPreserved: false); }
        }
        if (!string.IsNullOrEmpty(credential)) text = text.Replace(credential, "[凭据已脱敏]", StringComparison.Ordinal);
        string safe = DiagnosticPrivacy.RedactText(text);
        bool redacted = !string.Equals(text, safe, StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(safe);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (options.Level == ParseDiagnosticLevel.Off || (options.Level != ParseDiagnosticLevel.Detailed && !includeAtStandard))
            return new("not_recorded", null, hash, bytes.Length, 0, false, redacted);
        int limit = Math.Clamp(maxUtf8Bytes ?? options.MaxTextUtf8Bytes, 1, 65_536);
        string captured = TruncateUtf8(safe, limit);
        return new("recorded", captured, hash, bytes.Length, Encoding.UTF8.GetByteCount(captured), captured.Length != safe.Length, redacted);
    }

    public static DiagnosticText UnknownText() => new("unknown", null, null, null, 0, false, false);

    /// <summary>按 Unicode 标量截断，不产生损坏的 UTF-8</summary>
    public static string TruncateUtf8(string text, int bytes)
    {
        int used = 0, chars = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > bytes) break;
            used += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        return text[..chars];
    }

    private sealed class Context(string operation, string runId, string? scanRunId, long? mediaItemId,
        IParseDiagnosticSink? sink, Context? previous) : IDisposable
    {
        public string Operation { get; } = operation;
        public string RunId { get; } = runId;
        public string? ScanRunId { get; } = scanRunId;
        public long? MediaItemId { get; set; } = mediaItemId;
        public IParseDiagnosticSink? Sink { get; } = sink;
        public long Sequence;
        public long WriteFailures;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Emit("operation.ended", new { elapsedMs = _elapsed.ElapsedMilliseconds, writeFailures = WriteFailures });
            Ambient.Value = previous;
        }
    }
}
