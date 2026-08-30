using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>AI 提供商最小连通性探测实现（复刻真实报文）</summary>
/// <remarks>
/// 统一委托协议策略 <see cref="IAiProtocol"/>（按 type 路由）发一条极小补全（user="ping"，MaxTokens=<see cref="ProbeMaxTokens"/>，不要求 JSON）：
/// 探测与真实调用走<b>同一套 wire / 鉴权 / 代理 / JSON 模式门控</b>，根除旧实现「发 /api/tags 或裸 ping 能过、但真实解析 400」的假阳性
/// （例如端点不支持 response_format）。
/// 不抛异常：所有失败归集到 Success=false + ErrorMessage；探测按免费档（IsFree=true）豁免 1 req/s 节流，避免拖慢手动测试。
/// 探测不要求结构化输出（StructuredJson=false）：只验「能否拿到一段补全」，不验 JSON 能力（JSON 能力差异留给真实解析/升级链兜底）。
/// </remarks>
internal sealed class AiProviderTester : IAiProviderTester
{
    /// <summary>连通性探测的最大生成 token 数（严禁取 1）</summary>
    /// <remarks>
    /// Claude（Anthropic Messages API）在 <c>max_tokens=1</c> 时会在产出任何内容块前就撞上限，返回
    /// <c>content:[]</c> + <c>stop_reason=max_tokens</c>，触发协议层「AI 响应缺少 content」误判——端点其实已
    /// 鉴权、路由、跑通模型，纯属探测把输出 token 饿死的<b>假阴性</b>（OpenAI/Ollama/Gemini 在 1 token 时会把
    /// 那 1 个 token 当文本吐出，故不暴露此坑，唯独 Claude 返回空内容块数组）。取 16 足以让任意厂商（含 Claude）
    /// 至少吐出一个文本 token；连通性测试只验「能否拿到一段补全」，成本/延迟仍可忽略（Haiku 4.5 输出 16 token ≈ $0.00008）。
    /// </remarks>
    private const int ProbeMaxTokens = 16;

    private readonly IReadOnlyDictionary<AiProviderType, IAiProtocol> _protocols;
    private readonly ILogger<AiProviderTester> _logger;

    public AiProviderTester(IEnumerable<IAiProtocol> protocols, ILogger<AiProviderTester> logger)
    {
        _protocols = protocols.ToDictionary(p => p.Protocol);
        _logger = logger;
    }

    public async Task<AiProviderTestResult> TestAsync(
        AiProviderType type,
        string baseUrl,
        string? apiKey,
        string model,
        int timeoutSeconds,
        bool useProxy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new AiProviderTestResult(false, null, 0, "BaseUrl 不能为空", null);
        if (string.IsNullOrWhiteSpace(model))
            return new AiProviderTestResult(false, null, 0, "Model 不能为空", null);
        if (!_protocols.TryGetValue(type, out IAiProtocol? protocol))
            return new AiProviderTestResult(false, null, 0, $"无协议实现：{type}", null);

        // 探测超时尊重用户配置（DTO 已钳到 [1,600]）：本地模型如 Ollama 首次加载可能数分钟，
        // 旧实现额外砍到 60s 会让冷启动时「测试连接」必然失败；走免费档豁免节流；不要求 JSON（最简连通性）
        int timeout = Math.Max(1, timeoutSeconds);
        AiProviderEndpoint endpoint = new(baseUrl, apiKey, model, timeout, useProxy, IsFree: true, StructuredJson: false);
        List<AiChatMessage> messages = [new("user", "ping")];

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            AiCompletion completion = await protocol.CompleteAsync(
                new AiProtocolRequest(endpoint, messages, JsonMode: false, Temperature: 0, MaxTokens: ProbeMaxTokens), ct);
            sw.Stop();
            string text = completion.Text ?? string.Empty;
            string snippet = text.Length > 200 ? text[..200] : text;
            return new AiProviderTestResult(true, 200, sw.Elapsed.TotalMilliseconds, null, snippet);
        }
        catch (AiProviderRateLimitException ex)
        {
            sw.Stop();
            return new AiProviderTestResult(false, 429, sw.Elapsed.TotalMilliseconds, ex.Message, null);
        }
        catch (AiProviderLogicalException ex)
        {
            sw.Stop();
            return new AiProviderTestResult(false, ex.HttpStatus, sw.Elapsed.TotalMilliseconds, ex.Message, null);
        }
        catch (AiProviderTransientException ex)
        {
            sw.Stop();
            return new AiProviderTestResult(false, null, sw.Elapsed.TotalMilliseconds, ex.Message, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new AiProviderTestResult(false, null, sw.Elapsed.TotalMilliseconds, $"超时（{timeoutSeconds}s）", null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "AI 提供商探测未预期异常 {Type} {BaseUrl}", type, baseUrl);
            return new AiProviderTestResult(false, null, sw.Elapsed.TotalMilliseconds, ex.Message, null);
        }
    }
}
