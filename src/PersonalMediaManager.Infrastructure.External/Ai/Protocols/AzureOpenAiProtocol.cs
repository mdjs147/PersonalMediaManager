using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Ai.Protocols;

/// <summary>Azure OpenAI 协议策略（deployment 路由 + api-key 头 + api-version）</summary>
/// <remarks>
/// 请求体 / 响应解析与 <see cref="OpenAiCompatibleProtocol"/> 同构（messages / choices[0].message.content / response_format），差异仅在：
///   - <b>端点</b>：POST {BaseUrl}/openai/deployments/{Model}/chat/completions?api-version=...（Model 当 deployment 名，body <b>不带</b> model 字段）。
///   - <b>鉴权</b>：<c>api-key</c> 请求头（非 Authorization: Bearer）；仅 ApiKey 非空才加。
///   - <b>api-version</b>：从 Endpoint.ExtraOptions(JSON) 的 <c>"apiVersion"</c> 读，缺省 <see cref="DefaultApiVersion"/>。
/// 节流按档 / JSON 模式按端点能力 / 失败映射均与其它协议一致。为不改动 OpenAiCompatibleProtocol（seed 产物），本类自带一份精简的 body / response 处理。
/// </remarks>
internal sealed class AzureOpenAiProtocol : IAiProtocol
{
    private const string ProtocolName = "AzureOpenAi";

    /// <summary>ExtraOptions 未指定 apiVersion 时的兜底版本（GA 稳定版）</summary>
    private const string DefaultApiVersion = "2024-06-01";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AzureOpenAiProtocol> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public AzureOpenAiProtocol(
        IHttpClientFactory httpFactory,
        ILogger<AzureOpenAiProtocol> logger,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public AiProviderType Protocol => AiProviderType.AzureOpenAi;

    public async Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
    {
        AiProviderEndpoint endpoint = request.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            throw new AiProviderLogicalException("BaseUrl 不能为空");
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            throw new AiProviderLogicalException("Model（deployment 名）不能为空");

        // 仅付费节点节流；免费档豁免
        if (!endpoint.IsFree)
            await _rateLimiter.ConsumeAsync(ct);

        HttpClient client = _httpFactory.CreateClient(
            endpoint.UseProxy ? ServiceCollectionExtensions.AiHttpClientProxy : ServiceCollectionExtensions.AiHttpClientDirect);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds));

        string apiVersion = ResolveApiVersion(endpoint.ExtraOptions);
        string path = $"/openai/deployments/{endpoint.Model}/chat/completions";
        string url = AiPromptHelpers.JoinUrl(endpoint.BaseUrl, path) + "?api-version=" + Uri.EscapeDataString(apiVersion);

        using HttpRequestMessage req = new(HttpMethod.Post, url);
        // 鉴权可选：Azure 用 api-key 头（非 Bearer）；仅 ApiKey 非空才加
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            req.Headers.TryAddWithoutValidation("api-key", endpoint.ApiKey);
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

            AiHttpFailureMapper.ThrowForStatus(ProtocolName, resp.StatusCode, body);

            return ExtractCompletion(body);
        }
    }

    /// <summary>从 ExtraOptions(JSON) 读 apiVersion，缺失 / 非法时回退默认版本</summary>
    private static string ResolveApiVersion(string? extraOptions)
    {
        if (string.IsNullOrWhiteSpace(extraOptions))
            return DefaultApiVersion;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(extraOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("apiVersion", out JsonElement v)
                && v.ValueKind == JsonValueKind.String)
            {
                string? s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s!;
            }
        }
        catch (JsonException)
        {
            // ExtraOptions 非法 JSON：服务层已校验，但此处仍兜底回退，绝不因配置脏数据抛异常
        }
        return DefaultApiVersion;
    }

    /// <summary>构造 chat/completions 请求体（与 OpenAI 同构，但 deployment 已在 URL，body 不带 model）</summary>
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
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["stream"] = false,
        };
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
