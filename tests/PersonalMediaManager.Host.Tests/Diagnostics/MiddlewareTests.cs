using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Diagnostics;

/// <summary>RequestIdMiddleware + ExceptionHandlerMiddleware：覆盖响应头注入、追踪 ID 回填、异常 → code 映射</summary>
public sealed class MiddlewareTests : IClassFixture<PmmHostFactory>
{
    private readonly PmmHostFactory _factory;

    public MiddlewareTests(PmmHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Response_AlwaysIncludes_XRequestIdHeader()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage resp = await client.GetAsync("/api/health");
        resp.Headers.Contains(RequestIdMiddleware.HeaderName).Should().BeTrue();
        resp.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Request_WithClientProvidedRequestId_IsEchoedBack()
    {
        HttpClient client = _factory.CreateClient();
        const string custom = "client-trace-123";
        HttpRequestMessage req = new(HttpMethod.Get, "/api/health");
        req.Headers.Add(RequestIdMiddleware.HeaderName, custom);

        HttpResponseMessage resp = await client.SendAsync(req);
        resp.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().Should().Be(custom);
    }

    [Fact]
    public async Task BusinessException_MappedTo_Code1000()
    {
        HttpClient client = await CreateAdminClientAsync();
        HttpResponseMessage resp = await client.GetAsync("/api/diag/boom?kind=business");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Be("业务规则失败示例");
        body.GetProperty("requestId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UnexpectedException_MappedTo_Code9000_WithoutLeakingDetail()
    {
        HttpClient client = await CreateAdminClientAsync();
        HttpResponseMessage resp = await client.GetAsync("/api/diag/boom?kind=server");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.ServerError);
        body.GetProperty("message").GetString().Should().Be("服务器内部错误");
        // 验证不泄漏栈/内部异常 message
        body.GetProperty("message").GetString().Should().NotContain("InvalidOperationException");
    }

    /// <summary>P2-r3.9：异常 message 含 apiKey 片段时，ExceptionHandlerMiddleware 应走 SensitiveDataRedactor 脱敏后返前端</summary>
    [Fact]
    public async Task BusinessException_With_Secret_IsRedacted_BeforeReturned()
    {
        HttpClient client = await CreateAdminClientAsync();
        HttpResponseMessage resp = await client.GetAsync("/api/diag/boom?kind=secret");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        string message = body.GetProperty("message").GetString()!;

        // 必须保留业务上下文「AI 校验失败」让前端有信息可展示
        message.Should().Contain("AI 校验失败");
        // 但不允许泄漏原 apiKey 值
        message.Should().NotContain("sk-1234567890abcdef");
        // 脱敏占位（apikey=*** 或 ***(N)）任一形式都接受
        message.Should().Contain("***");
    }

    /// <summary>登录 admin 拿 token 后注入 Authorization 头返回 HttpClient（BoomController 改为 Admin only 后需登录）</summary>
    /// <remarks>
    /// IClassFixture 复用同一 PmmHostFactory + db：第一次跑 Setup admin 成功，后续 testcase 共享 db 已 setup 完，
    /// /api/setup/admin 返 1000 BusinessError（"已存在 Admin"）；用 EnsureSuccessStatusCode 之外的判断容错，吞 1000 视为已就绪。
    /// </remarks>
    private async Task<HttpClient> CreateAdminClientAsync()
    {
        HttpClient client = _factory.CreateClient();

        // Setup 幂等：已 setup 时返 1000，吞掉视为就绪
        await client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await client.PostAsJsonAsync("/api/setup/complete", new { });

        HttpResponseMessage loginResp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        JsonElement login = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
