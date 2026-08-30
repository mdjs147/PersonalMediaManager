using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Ai.Protocols;

/// <summary>Ollama 本地协议策略（POST /api/chat，format=json，默认无鉴权）</summary>
/// <remarks>
/// 由旧 OllamaProvider 迁移而来，改为实现 <see cref="IAiProtocol"/> 协议原语（去掉 ParseContent，文本原样返回）。
/// 与 OpenAI 兼容协议的 wire 差异：
///   - 端点 POST {BaseUrl}/api/chat（BaseUrl 默认 http://localhost:11434）。
///   - 默认无鉴权（本地部署）；仅当 ApiKey 非空才加 Authorization: Bearer（套带鉴权反代的场景）。
///   - 请求体 <c>{model, messages, stream:false, options:{temperature}}</c>；JsonMode 且端点支持结构化时加 <c>format:"json"</c>。
///   - 响应为单对象 <c>{"message":{"content":"..."}}</c>（非 OpenAI 的 choices[] 结构）。
///   - token 用量读 prompt_eval_count / eval_count（缺失则 null）。
///   - 节流按档：仅付费节点（Endpoint.IsFree=false）走 1 req/s，免费档（本地常态）豁免。
/// 失败映射统一走 <see cref="AiHttpFailureMapper"/>，与 IAiProtocol 契约口径一致。
/// </remarks>
internal sealed class OllamaProtocol : IAiProtocol
{
    private const string ProtocolName = "Ollama";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OllamaProtocol> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public OllamaProtocol(
        IHttpClientFactory httpFactory,
        ILogger<OllamaProtocol> logger,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public AiProviderType Protocol => AiProviderType.Ollama;

    public async Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
    {
        AiProviderEndpoint endpoint = request.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            throw new AiProviderLogicalException("BaseUrl 不能为空");
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            throw new AiProviderLogicalException("Model 不能为空");

        // 仅付费节点节流；免费档豁免（本地不计费、无配额压力，无需 1 req/s 限速）
        if (!endpoint.IsFree)
            await _rateLimiter.ConsumeAsync(ct);

        HttpClient client = _httpFactory.CreateClient(
            endpoint.UseProxy ? ServiceCollectionExtensions.AiHttpClientProxy : ServiceCollectionExtensions.AiHttpClientDirect);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds));

        string url = AiPromptHelpers.JoinUrl(endpoint.BaseUrl, "/api/chat");
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        // 鉴权可选：本地原生 Ollama 无需鉴权；仅 ApiKey 非空才加 Bearer（兼容带鉴权反代）
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

    /// <summary>构造 Ollama /api/chat 请求体（messages 映射；JsonMode 且端点支持结构化时加 format:"json"）</summary>
    private static object BuildPayload(AiProtocolRequest request)
    {
        object[] messages = new object[request.Messages.Count];
        for (int i = 0; i < request.Messages.Count; i++)
        {
            AiChatMessage m = request.Messages[i];
            messages[i] = new { role = m.Role, content = m.Content };
        }

        Dictionary<string, object?> options = new() { ["temperature"] = request.Temperature };
        if (request.MaxTokens is int max && max > 0)
            options["num_predict"] = max;

        Dictionary<string, object?> payload = new()
        {
            ["model"] = request.Endpoint.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["options"] = options,
        };
        if (request.JsonMode && request.Endpoint.StructuredJson)
            payload["format"] = "json";

        return payload;
    }

    /// <summary>从 Ollama 响应取 message.content + 可选 token 用量（prompt_eval_count / eval_count）</summary>
    private static AiCompletion ExtractCompletion(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("message", out JsonElement msg))
                throw new AiProviderLogicalException("Ollama 响应缺少 message");
            if (!msg.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String)
                throw new AiProviderLogicalException("Ollama 响应 message.content 缺失");
            string text = content.GetString() ?? throw new AiProviderLogicalException("Ollama content 为空");

            int? promptTokens = TryGetTokenCount(root, "prompt_eval_count");
            int? completionTokens = TryGetTokenCount(root, "eval_count");
            return new AiCompletion(text, promptTokens, completionTokens);
        }
        catch (JsonException ex)
        {
            throw new AiProviderLogicalException($"Ollama 响应 JSON 解析失败：{ex.Message}", inner: ex);
        }
    }

    /// <summary>读顶层 token 计数（缺失 / 非数字时返回 null，不抛）</summary>
    private static int? TryGetTokenCount(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : null;
}
