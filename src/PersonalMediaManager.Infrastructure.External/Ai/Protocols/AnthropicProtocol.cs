using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Ai.Protocols;

/// <summary>Anthropic Messages 协议策略（POST /v1/messages）</summary>
/// <remarks>
/// 对接 Anthropic 官方 Messages API（端点 POST {BaseUrl}/v1/messages，BaseUrl 默认形态 https://api.anthropic.com），与 <see cref="OpenAiCompatibleProtocol"/> 同构：
///   - <b>鉴权可选</b>：仅 ApiKey 非空才加 <c>x-api-key</c> 请求头；恒定带 <c>anthropic-version: 2023-06-01</c>（API 必需的版本头）。
///   - <b>节流按档</b>：仅付费节点（Endpoint.IsFree=false）走 1 req/s 节流，免费节点豁免。
///   - <b>不做解析</b>：只返回首个 type=="text" 内容块的原始文本，{title,year,...} 反解交给上层。
/// 与 OpenAI 协议的 wire 差异（Anthropic 特有形状）：
///   - <b>system 顶层化</b>：Anthropic 的 <c>messages</c> 不接受 system 角色，须把所有 role=="system" 消息拼成顶层 <c>system</c> 字符串，<c>messages</c> 内仅保留 user/assistant。
///   - <b>max_tokens 必填</b>：请求体 <c>max_tokens</c> 为必需字段，缺省时取 1024（request.MaxTokens ?? 1024）。
///   - <b>无原生 JSON 模式</b>：Anthropic 无 response_format，JsonMode 为真时不额外加字段（仅靠上层 system 提示词约束），照常返回文本。
///   - <b>响应为内容块数组</b>：取 <c>content[]</c> 中首个 type=="text" 块的 <c>text</c>；token 用量读 usage.input_tokens / output_tokens。
/// 失败映射统一走 <see cref="AiHttpFailureMapper"/>，与 IAiProtocol 契约口径一致。
/// </remarks>
internal sealed class AnthropicProtocol : IAiProtocol
{
    private const string ProtocolName = "Anthropic";

    /// <summary>Anthropic API 必需的版本头值（长期稳定）</summary>
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>max_tokens 必填，调用端未指定时的兜底上限</summary>
    private const int DefaultMaxTokens = 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnthropicProtocol> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public AnthropicProtocol(
        IHttpClientFactory httpFactory,
        ILogger<AnthropicProtocol> logger,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public AiProviderType Protocol => AiProviderType.Anthropic;

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

        string url = AiPromptHelpers.JoinUrl(endpoint.BaseUrl, "/v1/messages");
        using HttpRequestMessage req = new(HttpMethod.Post, url);
        // 鉴权可选：仅 ApiKey 非空才加 x-api-key；anthropic-version 恒定必带
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            req.Headers.TryAddWithoutValidation("x-api-key", endpoint.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
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

    /// <summary>构造 Anthropic Messages 请求体（system 顶层化；messages 仅含 user/assistant；max_tokens 必填）</summary>
    /// <remarks>
    /// Anthropic 不支持 system 角色入 messages：把所有 role=="system" 的消息按顺序拼为顶层 <c>system</c> 字符串，
    /// 其余 user/assistant 按原序进 <c>messages</c>（content 为字符串）。JsonMode 无原生支持，故不下发任何额外字段。
    /// </remarks>
    private static object BuildPayload(AiProtocolRequest request)
    {
        StringBuilder systemBuilder = new();
        List<object> messages = new(request.Messages.Count);
        foreach (AiChatMessage m in request.Messages)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (systemBuilder.Length > 0)
                    systemBuilder.Append('\n');
                systemBuilder.Append(m.Content);
            }
            else
            {
                messages.Add(new { role = m.Role, content = m.Content });
            }
        }

        Dictionary<string, object?> payload = new()
        {
            ["model"] = request.Endpoint.Model,
            ["max_tokens"] = request.MaxTokens is int max && max > 0 ? max : DefaultMaxTokens,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
        };
        // system 顶层字段：仅当存在 system 消息时才下发（空字符串不发，避免无意义字段）
        if (systemBuilder.Length > 0)
            payload["system"] = systemBuilder.ToString();

        return payload;
    }

    /// <summary>从 Anthropic 响应取首个 type=="text" 内容块的 text + 可选 usage token 数</summary>
    /// <remarks>
    /// usage.input_tokens → PromptTokens；usage.output_tokens → CompletionTokens（缺失则 null）。
    /// <b>容错</b>：部分中转 / 代理端点以 HTTP 200 回错误体（官方 <c>{"type":"error","error":{...}}</c> 或裸 <c>error</c> 字段，
    /// 常见于余额不足 / 令牌无效 / 模型不可用），这类 2xx 错误体过不了 <see cref="AiHttpFailureMapper.ThrowForStatus"/>，
    /// 旧实现会笼统抛「AI 响应缺少 content」且丢弃报文，用户无从定位。此处：①先识别错误体并抛真实错误文案；
    /// ②content / text 缺失时把 stop_reason + 截断报文写进异常文案，并把完整报文塞入
    /// <see cref="AiCallDiagnostics.ResponseTextKey"/>（落 Audit_AiCall.ResponseText / 测试连接错误展示），便于定位「端点究竟返回了什么」。
    /// </remarks>
    private static AiCompletion ExtractCompletion(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            // ① 错误体优先：HTTP 200 但 body 是错误对象（中转代理常见），抛真实文案而非笼统「缺少 content」
            if (TryReadErrorMessage(root, out string? errMsg))
                throw LogicalWithBody($"AI 端点返回错误：{errMsg}", body);

            // ② 正常报文须有非空 content 数组
            if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                throw LogicalWithBody($"AI 响应缺少 content（stop_reason={ReadStopReason(root)}，body={AiPromptHelpers.Truncate(body, 200)}）", body);

            string? text = null;
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (block.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "text"
                    && block.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(text))
                throw LogicalWithBody($"AI 响应无 text 内容块（stop_reason={ReadStopReason(root)}，body={AiPromptHelpers.Truncate(body, 200)}）", body);

            int? promptTokens = null, completionTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = TryGetTokenCount(usage, "input_tokens");
                completionTokens = TryGetTokenCount(usage, "output_tokens");
            }

            return new AiCompletion(text!, promptTokens, completionTokens);
        }
        catch (JsonException ex)
        {
            throw LogicalWithBody($"AI 响应 JSON 解析失败：{ex.Message}（body={AiPromptHelpers.Truncate(body, 200)}）", body, ex);
        }
    }

    /// <summary>识别 Anthropic / 中转代理的错误体并提取错误文案</summary>
    /// <remarks>
    /// 兼容三种形态：①官方 <c>{"type":"error","error":{"type":"...","message":"..."}}</c>；
    /// ②中转常见 <c>{"error":{"message":"..."}}</c>；③裸字符串 <c>{"error":"..."}</c>。
    /// 正常成功报文为 <c>{"type":"message",...}</c> 且无顶层 <c>error</c>，故不会误判。
    /// </remarks>
    private static bool TryReadErrorMessage(JsonElement root, out string? message)
    {
        message = null;
        if (root.TryGetProperty("error", out JsonElement err))
        {
            if (err.ValueKind == JsonValueKind.Object)
            {
                string? em = err.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                string? et = err.TryGetProperty("type", out JsonElement ety) && ety.ValueKind == JsonValueKind.String ? ety.GetString() : null;
                message = (et, em) switch
                {
                    (not null, not null) => $"{et} - {em}",
                    (null, not null) => em,
                    (not null, null) => et,
                    _ => "(端点未提供错误详情)",
                };
                return true;
            }
            if (err.ValueKind == JsonValueKind.String)
            {
                message = err.GetString();
                return true;
            }
        }
        // 仅有 type==error（无 error 对象，少见）也按错误处理
        if (root.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() == "error")
        {
            message = "(端点标记 type=error 但未提供 error 详情)";
            return true;
        }
        return false;
    }

    /// <summary>读 stop_reason 文案（缺失 → "(无)"），仅用于异常诊断拼接</summary>
    private static string ReadStopReason(JsonElement root) =>
        root.TryGetProperty("stop_reason", out JsonElement s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "(空)"
            : "(无)";

    /// <summary>构造携带完整响应原文的逻辑异常（原文塞 Exception.Data，落 Audit_AiCall.ResponseText / 测试连接展示）</summary>
    private static AiProviderLogicalException LogicalWithBody(string message, string body, Exception? inner = null)
    {
        AiProviderLogicalException ex = new(message, inner: inner);
        ex.Data[AiCallDiagnostics.ResponseTextKey] = body;
        return ex;
    }

    /// <summary>从 usage 对象读 token 计数（缺失 / 非数字时返回 null，不抛）</summary>
    private static int? TryGetTokenCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : null;
}
