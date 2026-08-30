namespace PersonalMediaManager.Application.Contracts;

/// <summary>通用 AI 对话客户端契约（发任意 system+user prompt，拿 AI 原始文本）</summary>
/// <remarks>
/// 与 <see cref="IAiParser"/> 的区别：IAiParser.ParseAsync 锁死「媒体文件名解析」语义（system prompt 写死、
/// 返回结构化 AiParseResult）；本契约面向「自由问答」场景（如测试集 Triage 判定 / 规则正则生成），
/// 调用方自带 prompt 与 JSON 约束，本契约只负责选 provider + 发请求 + 回原文。
///
/// 实现位于 Infrastructure.External/Ai/AiChatClient.cs：
/// - 复用 IAiProviderResolver 拿有序 provider（已解密 ApiKey + 主备 + 代理开关）
/// - 主→备依次尝试，第一个成功即返回；全失败抛 <see cref="AiChatUnavailableException"/>
/// - 同时支持 OpenAI 兼容协议（Qwen/DeepSeek/OpenAiCompatible，POST /v1/chat/completions）
///   与 Ollama（POST /api/chat）两类协议
///
/// 写 Audit_AiCall（审计修复项 1，推翻早期「不落审计」决策）：对话类同为真实付费调用，与解析链同口径落审计
/// （provider / model / 耗时 / token / 成败 / 错误类型；MediaItemId=null 标识非解析类），监控页调用数 / 成功率 /
/// token 成本不再漏算，失败行进入 D3.4 健康统计扫描窗口（评估触发仍由解析链驱动）。
/// </remarks>
public interface IAiChatClient
{
    /// <summary>发起一次对话；按 resolver 顺序主→备遍历，返回首个成功 provider 的原始文本</summary>
    /// <exception cref="AiChatUnavailableException">无可用 provider 或全部 provider 失败</exception>
    Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default);
}

/// <summary>对话请求</summary>
/// <param name="SystemPrompt">系统提示词（角色与输出约束）</param>
/// <param name="UserPrompt">用户提示词（具体输入）</param>
/// <param name="JsonMode">是否要求 provider 强制 JSON 输出（OpenAI: response_format=json_object；Ollama: format=json）</param>
/// <param name="Temperature">采样温度（默认 0，追求确定性）</param>
public sealed record AiChatRequest(
    string SystemPrompt,
    string UserPrompt,
    bool JsonMode = true,
    double Temperature = 0);

/// <summary>对话结果</summary>
/// <param name="Content">AI 返回的原始文本（已剥离 Markdown 代码块围栏）</param>
/// <param name="ProviderId">命中的 provider 主键</param>
/// <param name="ProviderName">命中的 provider 展示名</param>
public sealed record AiChatResult(
    string Content,
    long ProviderId,
    string ProviderName);

/// <summary>无可用 AI 提供商或全部尝试失败</summary>
/// <remarks>调用方（如 AiAdvisor）捕获后转 BusinessException(1000) 返前端「AI 暂不可用」。</remarks>
public sealed class AiChatUnavailableException : Exception
{
    public AiChatUnavailableException(string message) : base(message) { }
}
