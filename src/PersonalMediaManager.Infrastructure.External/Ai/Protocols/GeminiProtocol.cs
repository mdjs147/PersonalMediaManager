using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Ai.Protocols;

/// <summary>Google Gemini 原生协议策略（POST /v1beta/models/{model}:generateContent）</summary>
/// <remarks>
/// 走 Gemini 原生 generateContent wire（非 OpenAI 兼容形状），与 <see cref="OpenAiCompatibleProtocol"/> 并列：
///   - <b>端点</b>：POST {BaseUrl}/v1beta/models/{model}:generateContent；BaseUrl 默认 https://generativelanguage.googleapis.com。
///   - <b>鉴权可选</b>：仅 ApiKey 非空才下发，<b>同时</b>走 query <c>?key={ApiKey}</c> 与 <c>x-goog-api-key</c> 头（两种官方支持的传法都带，最大化兼容反代）。
///   - <b>消息映射</b>：role=="system" 汇入 systemInstruction.parts；user→"user"、assistant→"model"，逐条进 contents[].parts[{text}]。
///   - <b>JSON 模式按端点能力</b>：JsonMode 且 Endpoint.StructuredJson 为真才下发 generationConfig.responseMimeType="application/json"（兼容不识别该字段的反代）。
///   - <b>节流按档</b>：仅付费节点（Endpoint.IsFree=false）走 1 req/s 节流，免费节点豁免。
///   - <b>不做解析</b>：只返回 candidates[0].content.parts 拼接文本，内容解析交给上层。
/// 失败映射统一走 <see cref="AiHttpFailureMapper"/>，与 IAiProtocol 契约口径一致。
/// </remarks>
internal sealed class GeminiProtocol : IAiProtocol
{
    private const string ProtocolName = "Gemini";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GeminiProtocol> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public GeminiProtocol(
        IHttpClientFactory httpFactory,
        ILogger<GeminiProtocol> logger,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public AiProviderType Protocol => AiProviderType.Gemini;

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

        // 路径含 model 名 + :generateContent 动作；key 可选走 query（同时再带 x-goog-api-key 头）
        string url = AiPromptHelpers.JoinUrl(endpoint.BaseUrl, $"/v1beta/models/{endpoint.Model}:generateContent");
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            url += "?key=" + Uri.EscapeDataString(endpoint.ApiKey);

        using HttpRequestMessage req = new(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            req.Headers.TryAddWithoutValidation("x-goog-api-key", endpoint.ApiKey);
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

    /// <summary>构造 Gemini generateContent 请求体（system→systemInstruction，user/assistant→contents；按端点能力决定 responseMimeType / maxOutputTokens）</summary>
    /// <remarks>assistant 角色映射为 Gemini 的 "model"；多条 system 消息按换行拼成单个 systemInstruction。</remarks>
    private static object BuildPayload(AiProtocolRequest request)
    {
        List<object> contents = new(request.Messages.Count);
        List<string> systemTexts = [];
        foreach (AiChatMessage m in request.Messages)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(m.Content))
                    systemTexts.Add(m.Content);
                continue;
            }
            // assistant → "model"；其余（含 user）一律 "user"
            string role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
            contents.Add(new { role, parts = new[] { new { text = m.Content } } });
        }

        Dictionary<string, object?> generationConfig = new()
        {
            ["temperature"] = request.Temperature,
        };
        // 仅当上层要 JSON 且端点声明支持结构化输出时才下发（部分反代不识别 responseMimeType 会 400）
        if (request.JsonMode && request.Endpoint.StructuredJson)
            generationConfig["responseMimeType"] = "application/json";
        if (request.MaxTokens is int max && max > 0)
            generationConfig["maxOutputTokens"] = max;

        Dictionary<string, object?> payload = new()
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };
        if (systemTexts.Count > 0)
            payload["systemInstruction"] = new { parts = new[] { new { text = string.Join('\n', systemTexts) } } };

        return payload;
    }

    /// <summary>从 Gemini 响应取 candidates[0].content.parts 拼接文本 + 可选 usageMetadata token 数</summary>
    private static AiCompletion ExtractCompletion(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                throw new AiProviderLogicalException("AI 响应缺少 candidates");
            JsonElement first = candidates[0];
            if (!first.TryGetProperty("content", out JsonElement content))
                throw new AiProviderLogicalException("AI 响应缺少 candidates[0].content");
            if (!content.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
                throw new AiProviderLogicalException("AI 响应缺少 content.parts");

            // 多 part 文本按序拼接（Gemini 可能分片返回）
            StringBuilder sb = new();
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String)
                    sb.Append(textEl.GetString());
            }
            string text = sb.ToString();
            if (string.IsNullOrEmpty(text))
                throw new AiProviderLogicalException("AI 响应 parts 文本为空");

            int? promptTokens = null, completionTokens = null;
            if (root.TryGetProperty("usageMetadata", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = TryGetTokenCount(usage, "promptTokenCount");
                completionTokens = TryGetTokenCount(usage, "candidatesTokenCount");
            }

            return new AiCompletion(text, promptTokens, completionTokens);
        }
        catch (JsonException ex)
        {
            throw new AiProviderLogicalException($"AI 响应 JSON 解析失败：{ex.Message}", inner: ex);
        }
    }

    /// <summary>从 usageMetadata 对象读 token 计数（缺失 / 非数字时返回 null，不抛）</summary>
    private static int? TryGetTokenCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : null;
}
