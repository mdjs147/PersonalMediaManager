using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Controllers;

/// <summary>统一全局搜索 Controller 集成烟雾 — 匿名可读 / 空 q / 缺 q / scopes 守门</summary>
/// <remarks>
/// 仅验证路由可达 + 匿名读 + 响应信封 + scopes 过滤生效；三域命中口径已在 Persistence.Tests 覆盖。
/// 每方法独立 PmmHostFactory（SetupGuard 静态缓存跨方法污染防御）。
/// </remarks>
public sealed class SearchControllerTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public SearchControllerTests()
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

    [Fact(DisplayName = "GET /api/search?q=x 匿名可读返回 200 + code0")]
    public async Task Search_With_Keyword_Public_Returns_200()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/search?q=x");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCode0(resp);
    }

    [Fact(DisplayName = "空 q → 200 三组空数组 + Counts 全 0")]
    public async Task Search_Blank_Keyword_Returns_Empty_Groups()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/search?q=%20%20");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement data = await ReadData(resp);
        data.GetProperty("history").GetArrayLength().Should().Be(0);
        data.GetProperty("library").GetArrayLength().Should().Be(0);
        data.GetProperty("review").GetArrayLength().Should().Be(0);
        data.GetProperty("counts").GetProperty("history").GetInt32().Should().Be(0);
        data.GetProperty("counts").GetProperty("library").GetInt32().Should().Be(0);
        data.GetProperty("counts").GetProperty("review").GetInt32().Should().Be(0);
    }

    [Fact(DisplayName = "缺 q（完全不传）→ 200 空结果")]
    public async Task Search_Missing_Keyword_Returns_Empty()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/search");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement data = await ReadData(resp);
        data.GetProperty("query").GetString().Should().BeEmpty();
        data.GetProperty("history").GetArrayLength().Should().Be(0);
    }

    [Fact(DisplayName = "scopes=history → review/library 域恒为空数组（即便无数据也证明键存在）")]
    public async Task Search_Scopes_History_Only_Keeps_Other_Groups_Empty()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/search?q=abc&scopes=history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement data = await ReadData(resp);
        data.GetProperty("history").ValueKind.Should().Be(JsonValueKind.Array);
        data.GetProperty("review").GetArrayLength().Should().Be(0);
        data.GetProperty("library").GetArrayLength().Should().Be(0);
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

    /// <summary>拆出 data 节点（克隆，doc 释放后仍可读）</summary>
    private static async Task<JsonElement> ReadData(HttpResponseMessage resp)
    {
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(0);
        return doc.RootElement.GetProperty("data").Clone();
    }
}
