using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;

namespace PersonalMediaManager.Infrastructure.Platform.Diagnostics;

/// <summary>有容量上限的本地 JSONL 诊断存储</summary>
public sealed partial class ParseDiagnosticFileSink : IParseDiagnosticSink, IParseDiagnosticArtifactSink, IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private string? _active;
    private FileStream? _stream;
    private DateTime _activeDate;
    private long _writeFailures;
    public ParseDiagnosticOptions Options { get; }
    internal Action? BeforeExportReadForTest { get; init; }

    public ParseDiagnosticFileSink(string directory, ParseDiagnosticOptions options)
    {
        _directory = Path.GetFullPath(directory);
        Options = options;
        Options.Normalize();
        LoadLevelOverride();
    }

    public void Write(ParseDiagnosticEvent value)
    {
        if (Options.Level == ParseDiagnosticLevel.Off) return;
        try
        {
            lock (_gate)
            {
                if (Options.Level == ParseDiagnosticLevel.Off) return;
                CheckDirectory();
                PrivateFileSystem.EnsureDirectory(_directory);
                PruneArtifacts(0, 0);
                string line = JsonSerializer.Serialize(value, ParseDiagnostics.JsonOptions);
                int bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (bytes > Options.MaxEventUtf8Bytes)
                {
                    // 保留关联信封，不能直接截断 JSON 行。
                    value = value with { Data = JsonSerializer.SerializeToElement(new
                    {
                        state = "not_recorded", reason = "event_size_limit", originalUtf8Bytes = bytes,
                        truncated = true, limitUtf8Bytes = Options.MaxEventUtf8Bytes,
                    }) };
                    line = JsonSerializer.Serialize(value, ParseDiagnostics.JsonOptions);
                    bytes = Encoding.UTF8.GetByteCount(line) + 1;
                }
                if (_active is not null) PrivateFileSystem.RejectSymbolicLink(_active);
                if (_stream is null || (_active is not null && !File.Exists(_active))
                    || _activeDate != DateTime.UtcNow.Date || _stream.Length + bytes > Options.MaxFileBytes)
                {
                    _stream?.Dispose(); _stream = null;
                    _active = Path.Combine(_directory, $"parse-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jsonl");
                    Prune(bytes);
                    FileStreamOptions fileOptions = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read | FileShare.Delete };
                    if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = PrivateFileSystem.FilePermissions;
                    _stream = new FileStream(_active, fileOptions);
                    _activeDate = DateTime.UtcNow.Date;
                }
                Prune(bytes);
                _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
                _stream.Flush();
                Prune(0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.Security.SecurityException)
        { Interlocked.Increment(ref _writeFailures); }
    }

    /// <summary>导出一个运行、扫描或媒体的已保留证据</summary>
    public ParseReplayExport Export(string? runId, long? mediaItemId, string? scanRunId = null, CancellationToken ct = default)
    {
        if ((runId is null ? 0 : 1) + (mediaItemId is null ? 0 : 1) + (scanRunId is null ? 0 : 1) != 1)
            throw new ArgumentException("请且仅指定 runId、scanRunId 或 mediaItemId");
        foreach (string? id in new[] { runId, scanRunId })
            if (id is not null && (id.Length != 32 || !id.All(Uri.IsHexDigit))) throw new ArgumentException("运行 ID 格式无效");
        if (mediaItemId is <= 0) throw new ArgumentException("mediaItemId 必须为正数");

        // 锁内仅固定文件句柄和长度；实际读取、过滤和序列化均不占写入锁。
        List<(FileStream Stream, long Length)> snapshot = [];
        bool sourceTruncated = false;
        int unreadableLines = 0;
        try
        {
            lock (_gate)
            {
                CheckDirectory();
                PruneArtifacts(0, 0);
                long remaining = Math.Min(Options.MaxTotalBytes, 32 * 1024 * 1024);
                FileInfo[] files = Files().OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                if (files.Length > 128) sourceTruncated = true;
                foreach (FileInfo file in files.Take(128))
                {
                    ct.ThrowIfCancellationRequested();
                    PrivateFileSystem.RejectSymbolicLink(file.FullName);
                    if (remaining <= 0) { sourceTruncated = true; break; }
                    FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    long length = Math.Min(stream.Length, Math.Min(remaining, Options.MaxFileBytes));
                    if (length != stream.Length) sourceTruncated = true;
                    remaining -= length;
                    snapshot.Add((stream, length));
                }
            }
            BeforeExportReadForTest?.Invoke();
            List<ParseDiagnosticEvent> source = [];
            foreach ((FileStream stream, long length) in snapshot)
            {
                foreach (string? line in ReadBoundedLines(stream, length, Options.MaxEventUtf8Bytes, ct))
                {
                    if (line is null) { unreadableLines++; continue; }
                    if (source.Count >= 50_000) { sourceTruncated = true; break; }
                    try
                    {
                        ParseDiagnosticEvent? item = JsonSerializer.Deserialize<ParseDiagnosticEvent>(line, ParseDiagnostics.JsonOptions);
                        if (item is not null) source.Add(item);
                    }
                    catch (JsonException) { unreadableLines++; }
                }
                if (source.Count >= 50_000) break;
            }
            HashSet<string> mediaRuns = source.Where(e => e.MediaItemId == mediaItemId).Select(e => e.RunId).ToHashSet();
            List<ParseDiagnosticEvent> events = [];
            bool exportTruncated = false;
            long capturedBytes = 0;
            foreach (ParseDiagnosticEvent item in source.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence))
            {
                ct.ThrowIfCancellationRequested();
                if (runId is not null ? item.RunId != runId : scanRunId is not null ? item.ScanRunId != scanRunId : !mediaRuns.Contains(item.RunId)) continue;
                int bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item, ParseDiagnostics.JsonOptions));
                if (capturedBytes + bytes > 16 * 1024 * 1024 || events.Count >= 20_000) { exportTruncated = true; continue; }
                JsonElement safe = DiagnosticPrivacy.Sanitize(item.Data);
                events.Add(item with { Data = safe, DataRedacted = item.DataRedacted || safe.GetRawText() != item.Data.GetRawText() });
                capturedBytes += bytes;
            }
            string[] runs = events.Select(e => e.RunId).Distinct().ToArray();
            ParseReplayRunCompleteness[] perRun = runs.Select(id =>
            {
                ParseDiagnosticEvent[] trace = events.Where(e => e.RunId == id).ToArray();
                bool parse = trace.Any(e => e.Operation == "parse");
                ParseDiagnosticEvent[] inputs = trace.Where(e => e.Name == "parse.input").ToArray();
                bool available = inputs.Length == 1 && IsInputCaptured(inputs[0]);
                return new ParseReplayRunCompleteness(id, parse, trace.Any(e => e.Name == "operation.started"),
                    trace.Any(e => e.Name == "operation.ended"), parse ? available ? "captured_inputs_available" : "incomplete" : "not_applicable");
            }).ToArray();
            bool inputCaptured = perRun.Any(r => r.IsParseRun) && perRun.Where(r => r.IsParseRun).All(r => r.RuleInputReplay == "captured_inputs_available");
            IReadOnlyList<ParseDiagnosticArtifactExport> artifacts = ExportArtifacts(events, ct);
            return new(2, DateTimeOffset.UtcNow, "retained_local_evidence_only", events, new
            {
                started = perRun.Length > 0 && perRun.All(r => r.Started), ended = perRun.Length > 0 && perRun.All(r => r.Ended),
                inputCaptured, runs = perRun,
                truncatedEvents = events.Count(e => ContainsFlag(e.Data, "truncated", "true")),
                redactedEvents = events.Count(e => e.DataRedacted || ContainsFlag(e.Data, "redacted", "true")),
                notRecordedEvents = events.Count(e => ContainsFlag(e.Data, "state", "not_recorded")),
                unreadableLines, exportTruncated, sourceReadTruncated = sourceTruncated,
                storageWriteFailures = Interlocked.Read(ref _writeFailures), completePipelineReplay = false,
                artifactCount = artifacts.Count, incompleteArtifacts = artifacts.Count(a => a.State != "recorded"),
                aiEvidence = artifacts.Count > 0 && artifacts.All(a => a.State == "recorded")
                    && !sourceTruncated && !exportTruncated && Interlocked.Read(ref _writeFailures) == 0
                    && !events.Any(e => ContainsFlag(e.Data, "state", "not_recorded") || ContainsFlag(e.Data, "truncated", "true"))
                    ? "retained_redacted_artifacts_available" : "incomplete_or_not_enabled",
                ruleInputReplay = inputCaptured && !sourceTruncated && !exportTruncated ? "captured_inputs_available" : "incomplete",
                caveats = new[] { "保留窗口以外或进程中断前未落盘的事件无法恢复", "未记录、缺失、未知不等于空值或未发生", "AI正文按级别保存；脱敏/截断内容不能声称完整回放", "人工修正是人工来源，不是自动识别真值" },
            }, artifacts);
        }
        finally { foreach ((FileStream stream, _) in snapshot) stream.Dispose(); }
    }

    /// <summary>逐块读取有界行，损坏巨行不会整行分配</summary>
    private static IEnumerable<string?> ReadBoundedLines(Stream stream, long length, int maxLineBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream line = new();
        bool oversized = false;
        long remaining = length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) break;
            remaining -= count;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    yield return oversized ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                    line.SetLength(0); oversized = false;
                }
                else if (line.Length < maxLineBytes && !oversized) line.WriteByte(buffer[i]);
                else oversized = true;
            }
        }
        if (line.Length > 0 || oversized) yield return null; // 快照末尾半行不能当成完整事件。
    }

    private static bool IsInputCaptured(ParseDiagnosticEvent item)
    {
        if (item.DataRedacted || item.Data.ValueKind != JsonValueKind.Object
            || !item.Data.TryGetProperty("fileName", out JsonElement file)
            || !item.Data.TryGetProperty("relativeSegments", out JsonElement segments)
            || segments.ValueKind != JsonValueKind.Array) return false;
        return Captured(file) && segments.EnumerateArray().All(Captured);
        static bool Captured(JsonElement value) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("state", out JsonElement state) && state.GetString() == "recorded"
            && value.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
            && !ContainsFlag(value, "truncated", "true") && !ContainsFlag(value, "redacted", "true");
    }

    private static bool ContainsFlag(JsonElement data, string name, string value)
    {
        if (data.ValueKind == JsonValueKind.Object)
            return data.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ToString().Equals(value, StringComparison.OrdinalIgnoreCase) || ContainsFlag(p.Value, name, value));
        return data.ValueKind == JsonValueKind.Array && data.EnumerateArray().Any(e => ContainsFlag(e, name, value));
    }

    private void CheckDirectory()
    {
        PrivateFileSystem.RejectSymbolicLink(_directory);
        if (Path.GetDirectoryName(_directory) is { } parent) PrivateFileSystem.RejectSymbolicLink(parent);
    }

    private IEnumerable<FileInfo> Files()
    {
        CheckDirectory();
        return Directory.Exists(_directory)
            ? new DirectoryInfo(_directory).EnumerateFiles("parse-*.jsonl").Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).Take(129)
            : [];
    }

    private void Prune(int incomingBytes)
    {
        List<FileInfo> files = Files().Where(f => IsOwnedEventFile(f.Name)).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        long total = files.Sum(f => f.Length) + incomingBytes;
        int count = files.Count + (_active is not null && !File.Exists(_active) ? 1 : 0);
        foreach (FileInfo file in files)
        {
            if (file.FullName == _active) continue;
            if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-Options.RetentionDays) && total <= Options.MaxTotalBytes && count <= Options.MaxFiles) continue;
            PrivateFileSystem.RejectSymbolicLink(file.FullName);
            long length = file.Length; file.Delete(); total -= length; count--;
        }
    }

    public void Dispose() { lock (_gate) { _stream?.Dispose(); _stream = null; } }
}

/// <summary>本地可移交回放包</summary>
public sealed record ParseReplayExport(int SchemaVersion, DateTimeOffset ExportedAt, string Scope,
    IReadOnlyList<ParseDiagnosticEvent> Events, object Completeness, IReadOnlyList<ParseDiagnosticArtifactExport>? Artifacts = null);

/// <summary>逐运行的输入完整性</summary>
public sealed record ParseReplayRunCompleteness(string RunId, bool IsParseRun, bool Started, bool Ended, string RuleInputReplay);
