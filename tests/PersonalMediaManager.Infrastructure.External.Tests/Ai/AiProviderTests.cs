using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.Ai;
using PersonalMediaManager.Infrastructure.External.Ai.Protocols;
using PersonalMediaManager.Infrastructure.External.Tests.Tmdb;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

/// <summary>协议策略单测（IAiProtocol）— wire 路由 + 可选鉴权 + JSON 模式门控 + 失败语义 + 节流豁免</summary>
/// <remarks>
/// 协议化重构后，每个协议=一个 <see cref="IAiProtocol"/> 实现，<b>只返回助手原始文本</b>（AiCompletion），不做 {title,...} 反解（反解归 AiProtocolParser）。
/// 覆盖：
/// - Ollama 走 /api/chat、无鉴权（ApiKey 空）、解析 message.content
/// - OpenAI 兼容走 /v1/chat/completions、ApiKey 非空才加 Bearer、JsonMode+StructuredJson 才带 response_format
/// - Anthropic /v1/messages + x-api-key + anthropic-version + system 顶层化；Gemini :generateContent + ?key=；Azure deployment URL + api-version + api-key 头
/// - 失败映射（AiHttpFailureMapper）：5xx→Transient / 429→RateLimit / 4xx→Logical(status) / 响应结构非法→Logical
/// - 节流：付费档（IsFree=false）走 1 req/s；免费档（IsFree=true）豁免
/// 速率限制：测试用 1000/50ms 等价不节流，避免单测慢；进程级 1 req/s 由生产 DI 注入。
/// </remarks>
public sealed class AiProviderTests
{
    private const string OpenAiOkBody = """
        {"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"{\"title\":\"Inception\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":9}}
        """;
    private const string OllamaOkBody = """
        {"model":"llama3","message":{"role":"assistant","content":"{\"title\":\"Inception\"}"},"done":true,"prompt_eval_count":12,"eval_count":7}
        """;
    private const string AnthropicOkBody = """
        {"id":"msg","type":"message","role":"assistant","content":[{"type":"text","text":"{\"title\":\"Inception\"}"}],"usage":{"input_tokens":13,"output_tokens":5}}
        """;
    private const string GeminiOkBody = """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"title\":\"Inception\"}"}]}}],"usageMetadata":{"promptTokenCount":8,"candidatesTokenCount":4}}
        """;

    private static TokenBucketRateLimiter FastLimiter() => new(1000, TimeSpan.FromMilliseconds(50));
    private static List<AiChatMessage> Msgs() => [new("system", "你是媒体解析助手"), new("user", "Inception.2010.1080p.mkv")];
    private static AiProtocolRequest Req(AiProviderEndpoint ep, bool json = true, int? maxTokens = null) =>
        new(ep, Msgs(), JsonMode: json, Temperature: 0, MaxTokens: maxTokens);
    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // ───────────────── Ollama ─────────────────
    [Fact]
    public async Task Ollama_Success_ReturnsMessageContent_NoAuthHeader()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, OllamaOkBody);

        OllamaProtocol p = new(new StubHttpClientFactory(h), NullLogger<OllamaProtocol>.Instance, FastLimiter());
        AiProviderEndpoint ep = new("http://localhost:11434", ApiKey: null, "llama3", IsFree: true);

        AiCompletion r = await p.CompleteAsync(Req(ep));

        r.Text.Should().Contain("Inception");
        r.PromptTokens.Should().Be(12);
        r.CompletionTokens.Should().Be(7);
        h.Requests[0].RequestUri!.AbsoluteUri.Should().EndWith("/api/chat");
        h.Requests[0].Headers.Authorization.Should().BeNull();
        p.Protocol.Should().Be(AiProviderType.Ollama);
    }

    [Fact]
    public async Task Ollama_JsonMode_AddsFormatJson()
    {
        StubHttpMessageHandler h = new();
        string? body = null;
        h.EnqueueResponse(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Ok(OllamaOkBody); });

        OllamaProtocol p = new(new StubHttpClientFactory(h), NullLogger<OllamaProtocol>.Instance, FastLimiter());
        await p.CompleteAsync(Req(new("http://localhost:11434", null, "llama3", IsFree: true), json: true));

        body!.Should().Contain("\"format\":\"json\"");
    }

    [Fact]
    public async Task Ollama_500_ThrowsTransient()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "");
        OllamaProtocol p = new(new StubHttpClientFactory(h), NullLogger<OllamaProtocol>.Instance, FastLimiter());

        await ((Func<Task>)(() => p.CompleteAsync(Req(new("http://localhost:11434", null, "llama3")))))
            .Should().ThrowAsync<AiProviderTransientException>();
    }

    [Fact]
    public async Task Ollama_MissingMessage_ThrowsLogical()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"done":true}""");
        OllamaProtocol p = new(new StubHttpClientFactory(h), NullLogger<OllamaProtocol>.Instance, FastLimiter());

        await ((Func<Task>)(() => p.CompleteAsync(Req(new("http://localhost:11434", null, "llama3")))))
            .Should().ThrowAsync<AiProviderLogicalException>();
    }

    // ───────────────── OpenAI 兼容 ─────────────────
    [Fact]
    public async Task OpenAi_Success_Choices_WithBearer_AndResponseFormat()
    {
        StubHttpMessageHandler h = new();
        string? body = null;
        h.EnqueueResponse(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Ok(OpenAiOkBody); });

        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());
        AiProviderEndpoint ep = new("https://api.deepseek.com", "sk-test", "deepseek-chat", StructuredJson: true);

        AiCompletion r = await p.CompleteAsync(Req(ep, json: true));

        r.Text.Should().Contain("Inception");
        r.PromptTokens.Should().Be(11);
        h.Requests[0].RequestUri!.AbsoluteUri.Should().EndWith("/v1/chat/completions");
        h.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        h.Requests[0].Headers.Authorization!.Parameter.Should().Be("sk-test");
        body!.Should().Contain("response_format").And.Contain("json_object");
        p.Protocol.Should().Be(AiProviderType.OpenAiCompatible);
    }

    [Fact]
    public async Task OpenAi_StructuredJsonFalse_OmitsResponseFormat()
    {
        StubHttpMessageHandler h = new();
        string? body = null;
        h.EnqueueResponse(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Ok(OpenAiOkBody); });

        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());
        await p.CompleteAsync(Req(new("https://proxy.example.com", "sk-x", "gpt-4o-mini", StructuredJson: false), json: true));

        body!.Should().NotContain("response_format", "StructuredJson=false 时不下发 response_format（兼容不识别该字段的代理）");
    }

    [Fact]
    public async Task OpenAi_NoApiKey_OmitsAuthHeader()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);

        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());
        // 本地 OpenAI 兼容服务（如 LM Studio）可免 Key：ApiKey 空时统一不加鉴权头
        await p.CompleteAsync(Req(new("http://localhost:1234", ApiKey: null, "local-model", IsFree: true)));

        h.Requests[0].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task OpenAi_500_ThrowsTransient()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());

        await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://x", "sk", "m")))))
            .Should().ThrowAsync<AiProviderTransientException>();
    }

    [Fact]
    public async Task OpenAi_429_ThrowsRateLimit()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.TooManyRequests, "{}");
        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());

        await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://x", "sk", "m")))))
            .Should().ThrowAsync<AiProviderRateLimitException>();
    }

    [Fact]
    public async Task OpenAi_400_ThrowsLogical_WithHttpStatus()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.BadRequest, """{"error":"invalid model"}""");
        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());

        AiProviderLogicalException ex = (await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://x", "sk", "m")))))
            .Should().ThrowAsync<AiProviderLogicalException>()).Which;
        ex.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task OpenAi_MissingChoices_ThrowsLogical()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"object":"chat.completion"}""");
        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, FastLimiter());

        await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://x", "sk", "m")))))
            .Should().ThrowAsync<AiProviderLogicalException>();
    }

    // ───────────────── Anthropic ─────────────────
    [Fact]
    public async Task Anthropic_Success_Headers_SystemHoisted_ContentText()
    {
        StubHttpMessageHandler h = new();
        string? body = null;
        h.EnqueueResponse(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Ok(AnthropicOkBody); });

        AnthropicProtocol p = new(new StubHttpClientFactory(h), NullLogger<AnthropicProtocol>.Instance, FastLimiter());
        AiCompletion r = await p.CompleteAsync(Req(new("https://api.anthropic.com", "sk-ant", "claude-3-5-sonnet")));

        r.Text.Should().Contain("Inception");
        r.PromptTokens.Should().Be(13);
        h.Requests[0].RequestUri!.AbsoluteUri.Should().EndWith("/v1/messages");
        h.Requests[0].Headers.Contains("x-api-key").Should().BeTrue();
        h.Requests[0].Headers.Contains("anthropic-version").Should().BeTrue();
        body!.Should().Contain("\"system\"").And.Contain("max_tokens");
        p.Protocol.Should().Be(AiProviderType.Anthropic);
    }

    [Fact]
    public async Task Anthropic_429_ThrowsRateLimit()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.TooManyRequests, "{}");
        AnthropicProtocol p = new(new StubHttpClientFactory(h), NullLogger<AnthropicProtocol>.Instance, FastLimiter());

        await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://api.anthropic.com", "sk", "m")))))
            .Should().ThrowAsync<AiProviderRateLimitException>();
    }

    [Fact]
    public async Task Anthropic_200_WithErrorBody_ThrowsLogical_WithRealMessage()
    {
        // 中转/代理常见：HTTP 200 但 body 是错误对象（过不了 ThrowForStatus），须抛真实错误文案而非笼统「缺少 content」
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        AnthropicProtocol p = new(new StubHttpClientFactory(h), NullLogger<AnthropicProtocol>.Instance, FastLimiter());

        AiProviderLogicalException ex = (await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://api.anthropic.com", "sk", "m")))))
            .Should().ThrowAsync<AiProviderLogicalException>()).Which;
        ex.Message.Should().Contain("authentication_error").And.Contain("invalid x-api-key");
    }

    [Fact]
    public async Task Anthropic_200_MissingContent_ThrowsLogical_StashesBodyForAudit()
    {
        // 2xx 但无 content 数组：异常须携完整响应原文（落 Audit_AiCall.ResponseText），便于诊断「端点究竟返回了什么」
        const string body = """{"id":"msg","type":"message","role":"assistant","content":[]}""";
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, body);
        AnthropicProtocol p = new(new StubHttpClientFactory(h), NullLogger<AnthropicProtocol>.Instance, FastLimiter());

        AiProviderLogicalException ex = (await ((Func<Task>)(() => p.CompleteAsync(Req(new("https://api.anthropic.com", "sk", "m")))))
            .Should().ThrowAsync<AiProviderLogicalException>()).Which;
        ex.Message.Should().Contain("缺少 content");
        ex.Data[AiCallDiagnostics.ResponseTextKey].Should().Be(body);
    }

    [Fact]
    public async Task Tester_Anthropic_Probe_SendsEnoughTokens_NotOne()
    {
        // 回归护栏：连通性探测严禁发 max_tokens=1——Claude 在产出任何内容块前就撞上限，回 content:[] + stop_reason=max_tokens，
        // 被协议层误判「AI 响应缺少 content」（假阴性）。探测须给足 token（ProbeMaxTokens=16），端点正常即应报成功。
        StubHttpMessageHandler h = new();
        string? body = null;
        h.EnqueueResponse(req => { body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Ok(AnthropicOkBody); });

        AnthropicProtocol protocol = new(new StubHttpClientFactory(h), NullLogger<AnthropicProtocol>.Instance, FastLimiter());
        AiProviderTester tester = new([protocol], NullLogger<AiProviderTester>.Instance);

        AiProviderTestResult result = await tester.TestAsync(
            AiProviderType.Anthropic, "https://api.anthropic.com", "sk-ant", "claude-haiku-4-5", timeoutSeconds: 30, useProxy: false);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        body!.Should().Contain("\"max_tokens\":16");
    }

    // ───────────────── Gemini ─────────────────
    [Fact]
    public async Task Gemini_Success_KeyInQuery_ParsesParts()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, GeminiOkBody);

        GeminiProtocol p = new(new StubHttpClientFactory(h), NullLogger<GeminiProtocol>.Instance, FastLimiter());
        AiCompletion r = await p.CompleteAsync(Req(new("https://generativelanguage.googleapis.com", "g-key", "gemini-1.5-flash")));

        r.Text.Should().Contain("Inception");
        string uri = h.Requests[0].RequestUri!.AbsoluteUri;
        uri.Should().Contain(":generateContent").And.Contain("key=g-key");
        uri.Should().Contain("/v1beta/models/gemini-1.5-flash");
        p.Protocol.Should().Be(AiProviderType.Gemini);
    }

    // ───────────────── Azure OpenAI ─────────────────
    [Fact]
    public async Task Azure_Success_DeploymentUrl_ApiVersion_ApiKeyHeader()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);

        AzureOpenAiProtocol p = new(new StubHttpClientFactory(h), NullLogger<AzureOpenAiProtocol>.Instance, FastLimiter());
        // deployment 名取 Model；api-version 走 ExtraOptions
        AiProviderEndpoint ep = new("https://my.openai.azure.com", "az-key", "gpt4o-dep",
            ExtraOptions: """{"apiVersion":"2024-08-01-preview"}""");

        AiCompletion r = await p.CompleteAsync(Req(ep));

        r.Text.Should().Contain("Inception");
        string uri = h.Requests[0].RequestUri!.AbsoluteUri;
        uri.Should().Contain("/openai/deployments/gpt4o-dep/chat/completions");
        uri.Should().Contain("api-version=2024-08-01-preview");
        h.Requests[0].Headers.Contains("api-key").Should().BeTrue();
        h.Requests[0].Headers.Authorization.Should().BeNull("Azure 用 api-key 头而非 Bearer");
        p.Protocol.Should().Be(AiProviderType.AzureOpenAi);
    }

    [Fact]
    public async Task Azure_NoExtraOptions_UsesDefaultApiVersion()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);
        AzureOpenAiProtocol p = new(new StubHttpClientFactory(h), NullLogger<AzureOpenAiProtocol>.Instance, FastLimiter());

        await p.CompleteAsync(Req(new("https://my.openai.azure.com", "az-key", "dep1")));

        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("api-version=2024-06-01");
    }

    // ───────────────── 节流（按成本档位） ─────────────────
    [Fact]
    public async Task PaidNode_IsThrottled()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);
        TokenBucketRateLimiter limiter = new(1, TimeSpan.FromMilliseconds(200));
        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, limiter);
        AiProviderEndpoint ep = new("https://x", "sk", "m", IsFree: false);

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        await p.CompleteAsync(Req(ep));
        await p.CompleteAsync(Req(ep));
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(180, "付费档第二次调用应被节流到下一个 200ms 窗口");
    }

    [Fact]
    public async Task FreeNode_IsNotThrottled()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);
        h.EnqueueResponse(HttpStatusCode.OK, OpenAiOkBody);
        // 同样 1 token/200ms 限流器，但免费档应豁免 → 两次连发不阻塞
        TokenBucketRateLimiter limiter = new(1, TimeSpan.FromMilliseconds(200));
        OpenAiCompatibleProtocol p = new(new StubHttpClientFactory(h), NullLogger<OpenAiCompatibleProtocol>.Instance, limiter);
        AiProviderEndpoint ep = new("https://x", "sk", "m", IsFree: true);

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        await p.CompleteAsync(Req(ep));
        await p.CompleteAsync(Req(ep));
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(180, "免费档豁免节流，两次调用不应被限速");
    }
}
