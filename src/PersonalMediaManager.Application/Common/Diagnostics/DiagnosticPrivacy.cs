using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Common.Logging;

namespace PersonalMediaManager.Application.Common.Diagnostics;

/// <summary>诊断专用递归隐私边界</summary>
public static class DiagnosticPrivacy
{
    private static readonly Regex SecretKey = Pattern(@"^(authorization|proxy.?authorization|cookie|set.?cookie|headers?|api.?key|access.?token|refresh.?token|id.?token|token|password|passwd|pwd|secret|client.?secret|credentials?)$");
    private static readonly Regex ReasoningKey = Pattern(@"^(reasoning.*|thinking.*|chain.?of.?thought|thoughts?)$");
    private static readonly Regex PrivatePathKey = Pattern(@"^(fullPath|sourcePath|targetPath|watchRoot|conflictTarget|absolutePath)$");
    private static readonly Regex ThinkBlock = Pattern(@"<(think|thinking|reasoning|analysis)>[\s\S]*?(</\1>|$)");
    private static readonly Regex Header = Pattern(@"\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*[^\r\n]+");
    private static readonly Regex InlineSecret = Pattern(@"\b(api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|client[_-]?secret|password|secret|sig|signature)\s*[:=]\s*[""']?[^\s&""',;}]+");
    private static readonly Regex SensitiveStructure = Pattern(@"""(authorization|proxy.?authorization|cookie|set.?cookie|headers?|api.?key|access.?token|refresh.?token|id.?token|token|password|passwd|pwd|secret|client.?secret|credentials?)""\s*:");
    private static readonly Regex PrivateStructure = Pattern(@"""(reasoning[^""\r\n]*|thinking[^""\r\n]*|chain_of_thought)""\s*:");
    private static readonly Regex PrivateJsonField = Pattern(@"""(reasoning[^""\r\n]*|thinking[^""\r\n]*|chain_of_thought)""\s*:\s*""(?:[^""\\]|\\.)*(?:""|$)");
    private static readonly Regex Url = Pattern(@"https?://[^\s<>""']+");
    private static readonly Regex AbsolutePath = Pattern(@"(?<![\w:/])(?:[A-Za-z]:[\\/]|\\\\|/)[^\s""<>]+");
    private static readonly Regex ApiToken = Pattern(@"\bsk-[A-Za-z0-9_-]{8,}");
    private static Regex Pattern(string value) => new(value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));

    public static JsonElement Sanitize(object? data)
    {
        try
        {
            JsonNode? node = JsonSerializer.SerializeToNode(data, ParseDiagnostics.JsonOptions);
            return JsonSerializer.SerializeToElement(Clean(node, 0), ParseDiagnostics.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        { return JsonSerializer.SerializeToElement(new { state = "not_recorded", reason = "privacy_filter_failed" }); }
    }

    public static string RedactText(string text)
    {
        try { return CleanText(text, 0); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        { return "[脱敏无法安全完成，内容未记录]"; }
    }

    private static string CleanText(string text, int depth)
    {
        if (depth > 12) return "[嵌套超限，内容未记录]";
        string value = ThinkBlock.Replace(text, "[私有推理已省略]");
        string trimmed = value.TrimStart();
        bool structured = trimmed.StartsWith('{') || trimmed.StartsWith('[');
        if (!structured && (SensitiveStructure.IsMatch(value) || PrivateStructure.IsMatch(value)))
            return "[内嵌敏感结构，内容未记录]";
        if (structured)
        {
            try
            {
                JsonNode? json = JsonNode.Parse(value);
                if (json is JsonObject or JsonArray)
                {
                    JsonNode? clean = Clean(json.DeepClone(), depth + 1);
                    // 正常 JSON 原样保留；格式归一化不伪装成脱敏。
                    if (!JsonNode.DeepEquals(json, clean)) value = clean?.ToJsonString(ParseDiagnostics.JsonOptions) ?? "null";
                }
            }
            catch (JsonException) { if (PrivateStructure.IsMatch(value) || SensitiveStructure.IsMatch(value)) return "[私有推理结构不完整，内容未记录]"; }
        }
        value = PrivateJsonField.Replace(value, "\"private_content\":\"[已省略]\"");
        value = SensitiveDataRedactor.Redact(value);
        value = Header.Replace(value, "$1: [已脱敏]");
        value = InlineSecret.Replace(value, "$1=[已脱敏]");
        value = ApiToken.Replace(value, "[密钥已脱敏]");
        value = Url.Replace(value, m =>
        {
            if (!Uri.TryCreate(m.Value, UriKind.Absolute, out Uri? u)) return "[网址已脱敏]";
            if (string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment) && string.IsNullOrEmpty(u.UserInfo)) return m.Value;
            return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : $":{u.Port}")}{u.AbsolutePath}[认证和查询已省略]";
        });
        return AbsolutePath.Replace(value, "[绝对路径已省略]");
    }

    private static JsonNode? Clean(JsonNode? node, int depth)
    {
        if (depth > 12) return JsonValue.Create("[嵌套超限，内容未记录]");
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(p => p.Key).ToArray())
            {
                if (SecretKey.IsMatch(key) || ReasoningKey.IsMatch(key) || PrivatePathKey.IsMatch(key))
                    obj[key] = "[已省略]";
                else obj[key] = Clean(obj[key]?.DeepClone(), depth + 1);
            }
            return obj;
        }
        if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++) array[i] = Clean(array[i]?.DeepClone(), depth + 1);
            return array;
        }
        return node is JsonValue value && value.TryGetValue<string>(out string? s)
            ? JsonValue.Create(CleanText(s, depth + 1)) : node;
    }
}
