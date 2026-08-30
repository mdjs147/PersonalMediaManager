using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>IAiParser 实现：按协议路由到 IAiProtocol + 复用 AiPromptHelpers 拼提示词/反解</summary>
/// <remarks>
/// 把解析专用的 system / user 提示词（<see cref="AiPromptHelpers.SystemPrompt"/> / <see cref="AiPromptHelpers.BuildUserPrompt"/>）
/// 与 JSON 反解（<see cref="AiPromptHelpers.ParseContent"/>）封装在 External 层，使编排层只依赖 <see cref="IAiParser"/> 抽象，
/// 不反向耦合 Infrastructure.External 的工具类。
/// JsonMode 取端点能力 <see cref="AiProviderEndpoint.StructuredJson"/>；温度恒 0（解析要确定性输出）。
/// 多个相同 Protocol 的 <see cref="IAiProtocol"/> 不允许（DI 每协议一个）；ToDictionary 自然报重复。
/// </remarks>
internal sealed class AiProtocolParser : IAiParser
{
    private readonly IReadOnlyDictionary<AiProviderType, IAiProtocol> _protocols;

    public AiProtocolParser(IEnumerable<IAiProtocol> protocols)
    {
        _protocols = protocols.ToDictionary(p => p.Protocol);
    }

    public bool Supports(AiProviderType protocol) => _protocols.ContainsKey(protocol);

    public async Task<AiParseOutcome> ParseAsync(
        AiProviderType protocol,
        AiProviderEndpoint endpoint,
        AiParseRequest request,
        CancellationToken ct = default)
    {
        if (!_protocols.TryGetValue(protocol, out IAiProtocol? impl))
            throw new AiProviderLogicalException($"无 IAiProtocol 实现：{protocol}");

        // 请求原文：仅 user 提示词（system 恒定不入库省体积）；失败路径也补挂供诊断
        string userPrompt = AiPromptHelpers.BuildUserPrompt(request);
        List<AiChatMessage> messages =
        [
            new("system", AiPromptHelpers.SystemPrompt),
            new("user", userPrompt),
        ];

        AiCompletion completion;
        try
        {
            completion = await impl.CompleteAsync(
                new AiProtocolRequest(endpoint, messages, JsonMode: endpoint.StructuredJson, Temperature: 0),
                ct);
        }
        catch (Exception ex) when (ex is AiProviderTransientException or AiProviderRateLimitException or AiProviderLogicalException)
        {
            // 接口故障：补挂请求原文（响应体 + 状态码已由 AiHttpFailureMapper 在抛出前塞入 Exception.Data）
            ex.Data[AiCallDiagnostics.RequestTextKey] = userPrompt;
            throw;
        }

        try
        {
            AiParseResult result = AiPromptHelpers.ParseContent(completion.Text);
            // 字面溯源守护：剔除 AI 凭作品名幻觉、文件名 / 路径里根本没写的年份（多版本作品会锚到最早版本，污染 TMDB 匹配）
            result = AiPromptHelpers.GroundYear(result, request);
            return new AiParseOutcome(result, userPrompt, completion.Text, completion.PromptTokens, completion.CompletionTokens);
        }
        catch (AiProviderLogicalException ex)
        {
            // JSON 反解失败：补挂请求 + 响应原文 + token，便于诊断「AI 究竟返回了什么」
            ex.Data[AiCallDiagnostics.RequestTextKey] = userPrompt;
            ex.Data[AiCallDiagnostics.ResponseTextKey] = completion.Text;
            if (completion.PromptTokens is int pt) ex.Data[AiCallDiagnostics.PromptTokensKey] = pt;
            if (completion.CompletionTokens is int ctk) ex.Data[AiCallDiagnostics.CompletionTokensKey] = ctk;
            throw;
        }
    }
}
