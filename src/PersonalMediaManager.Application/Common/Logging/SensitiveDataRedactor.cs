using System.Text.RegularExpressions;

// 专属子命名空间（而非 Application.Common 根）：Host 侧多个文件同时 using Host.Logging 与 Application.Common，
// 与 Host 门面同名的本类若落在 Common 根会触发 CS0104 二义性；子命名空间无人顺带导入，从根上避开。
namespace PersonalMediaManager.Application.Common.Logging;

/// <summary>敏感字段脱敏器（日志写出侧与日志回放读取侧共用的唯一规则源）</summary>
/// <remarks>
/// 覆盖场景：
/// 1. QueryString 含 api_key / apiKey / token / access_token / key 等 → 替换值为「***(N)」。
///    裸 <c>key</c> 针对 Gemini 协议把 ApiKey 放 URL query 的形态（?key=AIza...，全仓唯一密钥进 query 的协议）；
///    依赖 \b 词边界 + 紧跟 <c>=</c> 双重约束：<c>monkey=</c>（k 前是单词字符无边界）与 <c>keyword=</c>（key 后不是 =）
///    均不会误伤；<c>x-key=</c> 这类连字符前缀会命中（连字符处成立词边界），按「宁可误脱也不泄露」接受。
/// 2. JWT Bearer Token：Authorization: Bearer xxx → 替换为「Bearer ***」。
/// 3. JSON 形式的敏感键："apiKey":"xxx" / "secret":"xxx" → 值替换为「***(N)」。
///    JSON 名单刻意不收裸 "key"：本系统 "key" 作为字段名大量承载非敏感的设置键名（System_Setting 键值对等），
///    收编会把设置类日志全部打码且无真实密钥流经该形态（ApiKey 序列化名为 apiKey，已在名单内）；
///    Gemini 的 ApiKey 仅以 URL query 形态出现，已被场景 1 覆盖（含 JSON 字符串内嵌 URL 的情况）。
/// 4. 通用 URL 内嵌敏感参数同 1。
/// 不做加密 / 不做白名单：以「宁可误脱也不泄露」为原则；命中即替换，长度信息保留为 `***(N)`。
/// 位置说明：原实现在 Host/Logging（仅 SignalRSink 等写出侧用）；日志回放（Application 层 LogQueryService）
/// 读取侧需要同一套规则，而 Application 禁止反向引用 Host，故实现下沉至此，Host 侧保留同名门面转发。
/// </remarks>
public static partial class SensitiveDataRedactor
{
    private const string Placeholder = "***";

    [GeneratedRegex(@"(?i)\b(apikey|api_key|api-key|token|access_token|refresh_token|id_token|secret|client_secret|password|pwd|pat|githubpat|github_pat|key)=([^&\s""']+)",
        RegexOptions.Compiled)]
    private static partial Regex QueryStringPattern();

    [GeneratedRegex(@"(?i)""(apikey|api_key|token|accessToken|refreshToken|secret|clientSecret|password|authorization|pat|gitHubPat|github_pat)""\s*:\s*""([^""\\]*(?:\\.[^""\\]*)*)""",
        RegexOptions.Compiled)]
    private static partial Regex JsonPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-_=]+(\.[A-Za-z0-9\-_=]+)*", RegexOptions.Compiled)]
    private static partial Regex BearerPattern();

    /// <summary>对日志消息文本做脱敏；输入为 null/empty 时原样返回</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        string redacted = QueryStringPattern().Replace(text, m => $"{m.Groups[1].Value}={MaskValue(m.Groups[2].Value)}");
        redacted = JsonPattern().Replace(redacted, m => $"\"{m.Groups[1].Value}\":\"{MaskValue(m.Groups[2].Value)}\"");
        redacted = BearerPattern().Replace(redacted, $"Bearer {Placeholder}");
        return redacted;
    }

    /// <summary>对单字段值做脱敏（用于 Header 值等已拆解的场景），保留长度提示</summary>
    public static string MaskValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Placeholder;
        return $"{Placeholder}({value.Length})";
    }
}
