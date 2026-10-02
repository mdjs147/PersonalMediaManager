using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PersonalMediaManager.Application.Common.Diagnostics;

/// <summary>解析诊断详细级别</summary>
public enum ParseDiagnosticLevel { Off, Standard, Detailed }

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
    }
}

/// <summary>正文捕获状态与明确的完整性信息</summary>
/// <remarks>哈希针对脱敏后的完整内容，绝不为密钥或私有推理生成指纹。missing=已观察为空；unknown=未能观察；not_recorded=策略不保存。UTF-8 长度仅针对可保存的脱敏内容。</remarks>
public sealed record DiagnosticText(string State, string? Text, string? Sha256, int? OriginalUtf8Bytes,
    int CapturedUtf8Bytes, bool Truncated, bool Redacted);

/// <summary>可离线关联的结构化事件</summary>
public sealed record ParseDiagnosticEvent(int SchemaVersion, DateTimeOffset Timestamp, string RunId,
    string? ScanRunId, long? MediaItemId, long Sequence, string Operation, string Name, JsonElement Data, bool DataRedacted = false);

/// <summary>本地诊断存储契约</summary>
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

    public static void Emit(string eventName, object? data = null)
    {
        Context? context = Ambient.Value;
        if (context?.Sink is null || context.Sink.Options.Level == ParseDiagnosticLevel.Off) return;
        try
        {
            JsonElement original = JsonSerializer.SerializeToElement(data, JsonOptions);
            JsonElement safe = DiagnosticPrivacy.Sanitize(original);
            context.Sink.Write(new(1, DateTimeOffset.UtcNow, context.RunId, context.ScanRunId,
                context.MediaItemId, Interlocked.Increment(ref context.Sequence), context.Operation, eventName, safe, original.GetRawText() != safe.GetRawText()));
        }
        catch (Exception) { Interlocked.Increment(ref context.WriteFailures); }
    }

    /// <summary>捕获可公开的输入与输出，标记策略缺省和截断</summary>
    public static DiagnosticText CaptureText(string? text, bool includeAtStandard = false, int? maxUtf8Bytes = null)
    {
        if (text is null) return new("missing", null, null, null, 0, false, false);
        ParseDiagnosticOptions options = Ambient.Value?.Sink?.Options ?? DefaultOptions;
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
