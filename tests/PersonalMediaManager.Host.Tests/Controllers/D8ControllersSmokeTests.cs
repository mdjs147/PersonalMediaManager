using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Controllers;

/// <summary>D8.1 Controllers 烟雾测试 — 路由可达 + 鉴权规则 + 业务异常映射</summary>
/// <remarks>
/// 不重复 Service 层端到端断言（已在 Persistence.Tests 覆盖），
/// 仅验证：路由前缀正确、AllowAnonymous 端点免登录可达、Admin 端点 401 未登录拦截、错误码包装格式。
/// 每个测试方法独立 PmmHostFactory（SetupGuard 静态缓存跨方法污染防御）。
/// </remarks>
public sealed class D8ControllersSmokeTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public D8ControllersSmokeTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _factory = new PmmHostFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SetupGuardMiddleware.ResetCacheForTest();
    }

    // ---------- Dashboard ----------

    [Fact]
    public async Task Dashboard_Stats_Public_Returns_200_AfterSetup()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/dashboard/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCode0(resp);
    }

    [Fact]
    public async Task Dashboard_Recent_Public_Returns_200()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/dashboard/recent?limit=10");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCode0(resp);
    }

    // ---------- Review ----------

    /// <summary>r3 第一波：撤销 r2 误收紧——待处理队列「看」按需求文档 §3.12 应匿名可读</summary>
    [Fact]
    public async Task Review_List_Public_Returns_200()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/review?page=1&pageSize=10");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCode0(resp);
    }

    [Fact]
    public async Task Review_Confirm_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/review/1/confirm",
            new { tmdbId = 1, mediaType = "movie", categoryId = 1, rowVersion = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Review_BatchConfirm_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/review/batch-confirm", new { items = new object[0] });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- History ----------

    [Fact]
    public async Task History_List_Public_Returns_200()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/history?page=1&pageSize=10");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCode0(resp);
    }

    [Fact]
    public async Task History_Rescan_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/history/1/rescan", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- Scan ----------

    [Fact]
    public async Task Scan_Trigger_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/scan/trigger", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Scan_Folder_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/scan/folder/1", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- Logs ----------

    [Fact]
    public async Task Logs_List_Public_Returns_200()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/logs?page=1&pageSize=10");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCode0(resp);
    }

    [Fact]
    public async Task Logs_Invalid_Level_Maps_To_1000()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/logs?level=Bogus");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(1000);
        doc.RootElement.GetProperty("message").GetString().Should().Be("日志级别取值非法");
    }

    private async Task CompleteSetupAsync()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
    }

    private static async Task AssertCode0(HttpResponseMessage resp)
    {
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(0);
        doc.RootElement.TryGetProperty("requestId", out _).Should().BeTrue();
    }
}
