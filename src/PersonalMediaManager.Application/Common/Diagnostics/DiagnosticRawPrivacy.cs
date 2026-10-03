using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Common.Diagnostics;

/// <summary>原格式诊断的隐私边界：只替换敏感 token 的字节区间，不重排其余 JSON。</summary>
public static class DiagnosticRawPrivacy
{
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private const string SensitiveKeys = @"auth|authentication|authorization|proxy.?authorization|cookie|set.?cookie|headers?|api.?key|access.?token|refresh.?token|id.?token|token|password|passwd|pwd|secret|client.?secret|credentials?|api.?token|signature|sig|analysis|reasoning.*|thinking.*|chain.?of.?thought|thoughts?|thought.?signature|fullPath|sourcePath|targetPath|watchRoot|absolutePath";
    private static readonly Regex Sensitive = Pattern("^(?:" + SensitiveKeys + ")$");
    private static readonly Regex SensitiveStructure = Pattern("\"(?:" + SensitiveKeys + ")\"\\s*:");
    private static readonly Regex Think = Pattern(@"<(think|thinking|reasoning|analysis)>[\s\S]*?(</\1>|$)");
    private static readonly Regex ReasoningTag = Pattern(@"</?(think|thinking|reasoning|analysis)\b");
    private static readonly Regex Bearer = Pattern(@"\b(Bearer|Basic)\s+[A-Za-z0-9+/_.=\-]+");
    private static readonly Regex Inline = Pattern(@"\b(api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|client[_-]?secret|password|secret|sig|signature)\s*[:=]\s*[""']?[^\s&""',;}]+");
    private static readonly Regex Header = Pattern(@"\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*[^\r\n]+");
    private static readonly Regex ApiKey = Pattern(@"\bsk-[A-Za-z0-9_-]{8,}");
    private static readonly Regex Url = Pattern(@"https?://[^\s<>""']+");
    private const string Omitted = "[隐私内容已省略]";

    public static RawDiagnosticRedaction Redact(string text, string? credential = null)
    {
        try
        {
            string safe = Clean(text, credential, 0);
            return new(safe, safe != text, true, "recorded");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        { return new("[隐私过滤无法安全完成，正文未保存]", true, false, "privacy_filter_failed"); }
    }

    private static string Clean(string text, string? credential, int depth)
    {
        if (depth > 16) throw new JsonException("诊断嵌套超限");
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('"'))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            List<(int Start, int End, byte[] Value)> edits = [];
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 64 });
            try
            {
                reader.Read();
                Walk(ref reader, bytes, edits, credential, depth);
                if (reader.Read()) throw new JsonException("诊断存在额外 JSON");
            }
            catch (JsonException)
            {
                if (SensitiveStructure.IsMatch(text) || text.Contains("\\u", StringComparison.OrdinalIgnoreCase) || text.Contains("\\\"", StringComparison.Ordinal)) throw;
                return Plain(text, credential);
            }
            if (edits.Count == 0) return text;
            using MemoryStream output = new();
            int offset = 0;
            foreach ((int Start, int End, byte[] Value) edit in edits.OrderBy(e => e.Start))
            {
                output.Write(bytes.AsSpan(offset, edit.Start - offset));
                output.Write(edit.Value); offset = edit.End;
            }
            output.Write(bytes.AsSpan(offset));
            return Encoding.UTF8.GetString(output.ToArray());
        }
        if (SensitiveStructure.IsMatch(text) || text.Contains("\\u", StringComparison.OrdinalIgnoreCase) || text.Contains("\\\"", StringComparison.Ordinal)) throw new JsonException("敏感嵌入结构未识别");
        return Plain(text, credential);
    }

    private static void Walk(ref Utf8JsonReader reader, byte[] bytes, List<(int Start, int End, byte[] Value)> edits,
        string? credential, int depth)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            Utf8JsonReader probe = reader;
            using JsonDocument document = JsonDocument.ParseValue(ref probe);
            JsonElement obj = document.RootElement;
            bool privatePart = obj.EnumerateObject().Any(property =>
                property.Name.Equals("thought", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.True
                || property.Name.Equals("type", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String
                    && new[] { "thinking", "redacted_thinking", "reasoning", "analysis" }.Contains(property.Value.GetString(), StringComparer.OrdinalIgnoreCase));
            if (privatePart)
            {
                edits.Add(((int)reader.TokenStartIndex, (int)probe.BytesConsumed, Encoding.UTF8.GetBytes("{\"privateContent\":\"[已省略]\"}")));
                reader = probe; return;
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                string name = reader.GetString()!;
                reader.Read();
                if (Sensitive.IsMatch(name))
                {
                    int start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    edits.Add((start, (int)reader.BytesConsumed, JsonSerializer.SerializeToUtf8Bytes(Omitted)));
                }
                else Walk(ref reader, bytes, edits, credential, depth);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) Walk(ref reader, bytes, edits, credential, depth);
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            string original = reader.GetString()!;
            string safe = Clean(original, credential, depth + 1);
            if (original != safe) edits.Add(((int)reader.TokenStartIndex, (int)reader.BytesConsumed, JsonSerializer.SerializeToUtf8Bytes(safe)));
        }
    }

    private static string Plain(string text, string? credential)
    {
        string value = string.IsNullOrEmpty(credential) ? text : text.Replace(credential, "[凭据已脱敏]", StringComparison.Ordinal);
        value = Think.Replace(value, Omitted);
        if (ReasoningTag.IsMatch(value)) throw new JsonException("嵌套或孤立推理标签");
        value = Header.Replace(value, "$1: [已脱敏]");
        value = Bearer.Replace(value, "$1 [已脱敏]");
        value = Inline.Replace(value, "$1=[已脱敏]");
        value = ApiKey.Replace(value, "[凭据已脱敏]");
        return Url.Replace(value, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri)) return "[网址已脱敏]";
            return string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo)
                ? match.Value : $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}[认证和查询已省略]";
        });
    }
}

public sealed record RawDiagnosticRedaction(string Text, bool Redacted, bool FormatPreserved, string State);
