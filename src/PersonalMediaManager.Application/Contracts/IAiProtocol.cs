using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>AI 协议级底层原语契约</summary>
/// <remarks>
/// 「发一组对话消息 → 拿回助手原始文本」的协议级最底层原语，<b>解析层（IAiParser）与聊天层（IAiChatClient）都建立在它之上</b>。
/// 一个实现对应一种 wire 协议（值 = <see cref="AiProviderType"/>），品牌（Qwen / DeepSeek 等）降为预设配置，不再各自一个实现。
///
/// 实现位于 Infrastructure.External/Ai/Protocols/：
///   OpenAiCompatibleProtocol → POST {BaseUrl}/v1/chat/completions（ApiKey 非空才加 Authorization: Bearer，统一可选鉴权）
///   （Ollama / Anthropic / Gemini / AzureOpenAi 协议由各自扇出 agent 落地）
///
/// 速率：免费节点（<see cref="AiProviderEndpoint.IsFree"/>=true）豁免节流；付费节点走进程级 1 req/s 节流（TokenBucketRateLimiter）。
///
/// 失败语义（三类异常由 AiHttpFailureMapper 统一映射）：
///   - 瞬时错误（DNS / TCP / 首字节超时 &lt; 5s / 5xx）→ 抛 <see cref="AiProviderTransientException"/>
///   - 限流 / 配额（HTTP 429）→ 抛 <see cref="AiProviderRateLimitException"/>
///   - 逻辑错误（4xx / 响应结构非法）→ 抛 <see cref="AiProviderLogicalException"/>
/// 本原语<b>不做内容解析</b>（不反解 {title,year,...}）：返回助手原始文本，解析交给上层。
/// </remarks>
public interface IAiProtocol
{
    /// <summary>协议类型（Resolver / 工厂用于按 wire 协议路由）</summary>
    AiProviderType Protocol { get; }

    /// <summary>发一组对话消息，拿回助手原始文本</summary>
    /// <exception cref="AiProviderTransientException">瞬时错误（DNS/TCP/首字节超时 &lt; 5s / 5xx）</exception>
    /// <exception cref="AiProviderRateLimitException">限流 / 配额（HTTP 429）</exception>
    /// <exception cref="AiProviderLogicalException">逻辑错误（4xx / 响应结构非法）</exception>
    Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default);
}

/// <summary>协议补全结果（助手原始文本 + 可选 token 用量）</summary>
/// <param name="Text">助手返回的原始文本（未做任何解析）</param>
/// <param name="PromptTokens">提示词消耗的 token 数（厂商返回则填，否则 null）</param>
/// <param name="CompletionTokens">补全消耗的 token 数（厂商返回则填，否则 null）</param>
public sealed record AiCompletion(
    string Text,
    int? PromptTokens = null,
    int? CompletionTokens = null);

/// <summary>协议补全入参（调用端配置 + 对话消息 + 生成参数）</summary>
/// <param name="Endpoint">调用端配置（BaseUrl / ApiKey / Model / 超时 / 代理 / 免费档 / 结构化 JSON / 扩展参数）</param>
/// <param name="Messages">对话消息序列（system / user / assistant），按顺序发送</param>
/// <param name="JsonMode">是否要求模型仅输出 JSON（OpenAI 协议映射为 response_format=json_object）</param>
/// <param name="Temperature">采样温度（默认 0，确定性输出）</param>
/// <param name="MaxTokens">最大生成 token 数（null = 不限制，由厂商默认）</param>
public sealed record AiProtocolRequest(
    AiProviderEndpoint Endpoint,
    IReadOnlyList<AiChatMessage> Messages,
    bool JsonMode,
    double Temperature = 0,
    int? MaxTokens = null);

/// <summary>单条对话消息</summary>
/// <param name="Role">角色："system" | "user" | "assistant"</param>
/// <param name="Content">消息正文</param>
public sealed record AiChatMessage(
    string Role,
    string Content);
