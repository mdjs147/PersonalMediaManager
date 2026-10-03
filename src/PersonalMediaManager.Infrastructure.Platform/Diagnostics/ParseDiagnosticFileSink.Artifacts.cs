using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Dtos.Diagnostics;

namespace PersonalMediaManager.Infrastructure.Platform.Diagnostics;

public sealed partial class ParseDiagnosticFileSink
{
    private static bool IsOwnedEventFile(string name) => Regex.IsMatch(name, @"^parse-\d{17}-[a-f0-9]{32}\.jsonl$", RegexOptions.CultureInvariant);
    private static bool IsArtifactId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string ArtifactPath(string id) => Path.Combine(_directory, $"body-{id}.txt");
    private string LevelPath => Path.Combine(_directory, "diagnostic-settings.json");

    /// <summary>每个阶段独立的 UTF-8 正文；超过限额整段不保存，绝不把前缀冒充完整内容。</summary>
    public DiagnosticText StoreArtifact(DiagnosticText metadata, string redactedText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(redactedText);
        if (bytes.Length > Options.MaxArtifactUtf8Bytes)
            return metadata with { State = "not_recorded", Reason = "artifact_size_limit", Truncated = true };
        try
        {
            lock (_gate)
            {
                if (Options.Level != ParseDiagnosticLevel.Full)
                    return metadata with { State = "not_recorded", Reason = "level_changed" };
                CheckDirectory(); PrivateFileSystem.EnsureDirectory(_directory);
                PruneArtifacts(bytes.Length, 1);
                string id = Guid.NewGuid().ToString("N");
                string path = ArtifactPath(id);
                try
                {
                    using FileStream file = PrivateFileSystem.CreateNew(path);
                    file.Write(bytes); file.Flush();
                }
                catch { if (File.Exists(path)) { PrivateFileSystem.RejectSymbolicLink(path); File.Delete(path); } throw; }
                return metadata with { State = "recorded", ArtifactId = id, CapturedUtf8Bytes = bytes.Length };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.Security.SecurityException)
        {
            Interlocked.Increment(ref _writeFailures);
            return metadata with { State = "not_recorded", Reason = "artifact_write_failed" };
        }
    }

    public ParseDiagnosticSettingsDto GetSettings() => new(
        Options.Level.ToString(), Options.MaxTextUtf8Bytes, Options.MaxEventUtf8Bytes,
        Options.MaxFileBytes, Options.MaxTotalBytes, Options.RetentionDays, Options.MaxFiles,
        Options.MaxArtifactUtf8Bytes, Options.MaxArtifactTotalBytes, Options.MaxArtifacts,
        Interlocked.Read(ref _writeFailures),
        "脱敏后的实际原格式正文；密钥、认证与私有推理不保存；不完整原因明确标记");

    /// <summary>管理端显式切换；先安全落盘再应用，失败不伪报保存成功。</summary>
    public ParseDiagnosticSettingsDto SetLevel(string level)
    {
        if (!Enum.GetNames<ParseDiagnosticLevel>().Contains(level, StringComparer.Ordinal)
            || !Enum.TryParse(level, false, out ParseDiagnosticLevel parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException("诊断级别必须为 Off、Standard、Detailed 或 Full");
        lock (_gate)
        {
            CheckDirectory(); PrivateFileSystem.EnsureDirectory(_directory);
            PrivateFileSystem.RejectSymbolicLink(LevelPath);
            string temporary = Path.Combine(_directory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream file = PrivateFileSystem.CreateNew(temporary))
                    JsonSerializer.Serialize(file, new { level = parsed.ToString() });
                File.Move(temporary, LevelPath, overwrite: true);
                Options.Level = parsed;
            }
            finally { if (File.Exists(temporary)) { PrivateFileSystem.RejectSymbolicLink(temporary); File.Delete(temporary); } }
        }
        return GetSettings();
    }

    private void LoadLevelOverride()
    {
        try
        {
            CheckDirectory(); PrivateFileSystem.RejectSymbolicLink(LevelPath);
            if (!File.Exists(LevelPath)) return;
            if (new FileInfo(LevelPath).Length > 1024) throw new IOException("诊断设置超限");
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(LevelPath));
            if (settings.RootElement.TryGetProperty("level", out JsonElement value)
                && Enum.TryParse(value.GetString(), false, out ParseDiagnosticLevel level) && Enum.IsDefined(level)) Options.Level = level;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { Interlocked.Increment(ref _writeFailures); }
    }

    private IEnumerable<FileInfo> ArtifactFiles() => Directory.Exists(_directory)
        ? new DirectoryInfo(_directory).EnumerateFiles("body-*.txt")
            .Where(file => file.Name.Length == 41 && IsArtifactId(file.Name[5..^4]) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        : [];

    private void PruneArtifacts(int incomingBytes, int incomingCount)
    {
        FileInfo[] files = ArtifactFiles().OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        long bytes = files.Sum(f => f.Length) + incomingBytes;
        int count = files.Length + incomingCount;
        foreach (FileInfo file in files)
        {
            if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-Options.RetentionDays)
                && bytes <= Options.MaxArtifactTotalBytes && count <= Options.MaxArtifacts) continue;
            PrivateFileSystem.RejectSymbolicLink(file.FullName);
            long length = file.Length; file.Delete(); bytes -= length; count--;
        }
    }

    private IReadOnlyList<ParseDiagnosticArtifactExport> ExportArtifacts(IReadOnlyList<ParseDiagnosticEvent> events, CancellationToken ct)
    {
        Dictionary<string, (string? Hash, int? Length)> references = new(StringComparer.Ordinal);
        foreach (ParseDiagnosticEvent item in events) Collect(item.Data);
        List<ParseDiagnosticArtifactExport> output = [];
        long remaining = Math.Min(Options.MaxArtifactTotalBytes, 64 * 1024 * 1024);
        foreach (KeyValuePair<string, (string? Hash, int? Length)> reference in references)
        {
            string id = reference.Key;
            (string? Hash, int? Length) expected = reference.Value;
            ct.ThrowIfCancellationRequested();
            if (!IsArtifactId(id)) { output.Add(new(id, "unavailable", null, "invalid_artifact_id")); continue; }
            if (expected.Hash is null || expected.Hash.Length != 64 || !expected.Hash.All(Uri.IsHexDigit) || expected.Length is null or < 0)
            { output.Add(new(id, "unavailable", null, "invalid_artifact_reference")); continue; }
            try
            {
                CheckDirectory(); string path = ArtifactPath(id); PrivateFileSystem.RejectSymbolicLink(path);
                using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length > Options.MaxArtifactUtf8Bytes || file.Length > remaining)
                { output.Add(new(id, "not_recorded", null, "export_size_limit")); continue; }
                byte[] bytes = new byte[file.Length]; file.ReadExactly(bytes); remaining -= bytes.Length;
                string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (hash != expected.Hash || bytes.Length != expected.Length)
                { output.Add(new(id, "unavailable", null, "artifact_integrity_mismatch")); continue; }
                string original = new UTF8Encoding(false, true).GetString(bytes);
                RawDiagnosticRedaction verified = DiagnosticRawPrivacy.Redact(original);
                if (verified.State != "recorded" || verified.Text != original)
                { output.Add(new(id, "unavailable", null, "export_privacy_verification_failed")); continue; }
                output.Add(new(id, "recorded", original, null));
            }
            catch (FileNotFoundException) { output.Add(new(id, "unavailable", null, "expired_or_missing")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or global::System.Security.SecurityException)
            { output.Add(new(id, "unavailable", null, "artifact_read_failed")); }
        }
        return output;

        void Collect(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("artifactId", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    (string? Hash, int? Length) reference = (
                        value.TryGetProperty("sha256", out JsonElement hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null,
                        value.TryGetProperty("capturedUtf8Bytes", out JsonElement length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out int size) ? size : null);
                    string key = id.GetString()!;
                    if (references.TryGetValue(key, out (string? Hash, int? Length) previous) && previous != reference)
                        references[key] = (null, null);
                    else references[key] = reference;
                }
                foreach (JsonProperty property in value.EnumerateObject()) Collect(property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement item in value.EnumerateArray()) Collect(item);
        }
    }
}

public sealed record ParseDiagnosticArtifactExport(string ArtifactId, string State, string? Text, string? Reason);
