using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Ai.Protocols;

/// <summary>OpenAI 兼容协议策略（POST /v1/chat/completions）</summary>
/// <remarks>
/// 覆盖 OpenAI / Qwen / DeepSeek / 第三方代理等所有走 OpenAI chat/completions 形状的端点，是协议化重构的参考实现。
/// 相对旧 OpenAiCompatibleProviderBase 的关键变化：
///   - <b>去品牌</b>：不再按厂商建类，统一一个策略；厂商差异由预设（BaseUrl / Model / 免费档）承载。
///   - <b>鉴权可选</b>：仅当 ApiKey 非空才加 Authorization: Bearer（旧实现 ApiKey 必填硬抛，现统一可选）。
///   - <b>节流按档</b>：仅付费节点（Endpoint.IsFree=false）走 1 req/s 节流，免费节点豁免。
///   - <b>不做解析</b>：只返回 choices[0].message.content 原始文本（去掉 ParseContent），解析交给上层。
///   - <b>JSON 模式按端点能力</b>：JsonMode 且 Endpoint.StructuredJson 为真才下发 response_format=json_object（兼容不识别该字段的代理）。
/// 失败映射统一走 <see cref="AiHttpFailureMapper"/>，与 IAiProtocol 契约口径一致。
/// </remarks>
internal sealed class OpenAiCompatibleProtocol : IAiProtocol
{
    private const string ProtocolName = "OpenAiCompatible";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenAiCompatibleProtocol> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public OpenAiCompatibleProtocol(
        IHttpClientFactory httpFactory,
        ILogger<OpenAiCompatibleProtocol> logger,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public AiProviderType Protocol => AiProviderType.OpenAiCompatible;

    public async Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
    {
        AiProviderEndpoint endpoint = request.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            throw new AiProviderLogicalException("BaseUrl 不能为空");
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            throw new AiProviderLogicalException("Model 不能为空");

        // 仅付费节点节流；免费档豁免（无配额压力，无需 1 req/s 限速）
        if (!endpoint.IsFree)
            await _rateLimiter.ConsumeAsync(ct);

        HttpClient client = _httpFactory.CreateClient(
            endpoint.UseProxy ? ServiceCollectionExtensions.AiHttpClientProxy : ServiceCollectionExtensions.AiHttpClientDirect);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds));

        string url = AiPromptHelpers.JoinUrl(endpoint.BaseUrl, "/v1/chat/completions");
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        // 鉴权可选：仅 ApiKey 非空才加 Bearer（Ollama / 无鉴权代理留空）
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        req.Content = JsonContent.Create(BuildPayload(request));

        HttpResponseMessage resp;
        DateTimeOffset sentAt = DateTimeOffset.UtcNow;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw AiHttpFailureMapper.MapSendException(ProtocolName, ex, sentAt);
        }

        using (resp)
        {
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct); }
            catch { body = string.Empty; }

            // 状态错误统一映射（429 → 限流 / 5xx → 瞬时 / 4xx → 逻辑）
            AiHttpFailureMapper.ThrowForStatus(ProtocolName, resp.StatusCode, body);

            return ExtractCompletion(body);
        }
    }

    /// <summary>构造 OpenAI chat/completions 请求体（messages 从协议入参映射；按端点能力决定 response_format / max_tokens）</summary>
    private static object BuildPayload(AiProtocolRequest request)
    {
        object[] messages = new object[request.Messages.Count];
        for (int i = 0; i < request.Messages.Count; i++)
        {
            AiChatMessage m = request.Messages[i];
            messages[i] = new { role = m.Role, content = m.Content };
        }

        Dictionary<string, object?> payload = new()
        {
            ["model"] = request.Endpoint.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["stream"] = false,
        };
        // 仅当上层要 JSON 且端点声明支持结构化输出时才下发（部分代理不识别 response_format 会 400）
        if (request.JsonMode && request.Endpoint.StructuredJson)
            payload["response_format"] = new { type = "json_object" };
        if (request.MaxTokens is int max && max > 0)
            payload["max_tokens"] = max;

        return payload;
    }

    /// <summary>从 OpenAI 标准响应取 choices[0].message.content + 可选 usage token 数</summary>
    private static AiCompletion ExtractCompletion(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new AiProviderLogicalException("AI 响应缺少 choices");
            JsonElement first = choices[0];
            if (!first.TryGetProperty("message", out JsonElement message))
                throw new AiProviderLogicalException("AI 响应缺少 choices[0].message");
            if (!message.TryGetProperty("content", out JsonElement contentEl) || contentEl.ValueKind != JsonValueKind.String)
                throw new AiProviderLogicalException("AI 响应缺少 message.content");
            string text = contentEl.GetString() ?? throw new AiProviderLogicalException("AI 响应 content 为空");

            int? promptTokens = null, completionTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = TryGetTokenCount(usage, "prompt_tokens");
                completionTokens = TryGetTokenCount(usage, "completion_tokens");
            }

            return new AiCompletion(text, promptTokens, completionTokens);
        }
        catch (JsonException ex)
        {
            throw new AiProviderLogicalException($"AI 响应 JSON 解析失败：{ex.Message}", inner: ex);
        }
    }

    /// <summary>从 usage 对象读 token 计数（缺失 / 非数字时返回 null，不抛）</summary>
    private static int? TryGetTokenCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : null;
}
