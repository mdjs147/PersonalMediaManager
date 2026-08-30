using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Controllers;

/// <summary>Review TmdbSearch 查询模型绑定回归测试</summary>
/// <remarks>
/// 守护 bug：GET /review/{id}/tmdb-search 的 action 形参名若与查询键 query 同名，
/// 复杂类型模型绑定会把 query 当作前缀（查 query.Query），导致必填 Query 绑定失败，
/// [ApiController] 自动返回 400 —— 人工处理界面「TMDB 候选 → 搜索」点击即报 HTTP 400。
///
/// 判定：以 Admin 登录（tmdb-search 已收紧为需登录，防匿名刷 TMDB 配额）后，用合法 query + 不存在的 id 调用。
/// 绑定正确时请求会进入 service，service 校验「记录不存在」抛 BusinessException → 200 + code 1000。
/// 若绑定失败（bug 复现）则在 action 执行前就返 400。
/// </remarks>
public sealed class ReviewTmdbSearchBindingTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public ReviewTmdbSearchBindingTests()
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

    [Fact]
    public async Task TmdbSearch_With_Query_And_Type_And_Year_Binds_And_Reaches_Service()
    {
        await LoginAsAdminAsync();

        HttpResponseMessage resp = await _client.GetAsync(
            "/api/review/999999/tmdb-search?query=%E9%97%B4%E8%B0%8D%E8%BF%87%E5%AE%B6%E5%AE%B6&type=both&year=2022");

        // 绑定成功才会进入 service 命中「记录不存在」；绑定失败会是 400
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "查询参数应能绑定到 TmdbSearchListQuery，不应在模型绑定阶段 400");
        string raw = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(
            ApiCode.BusinessError, "合法 query 已绑定 → 进入 service → 不存在的 id 命中 1000");
        doc.RootElement.GetProperty("message").GetString().Should().Be(
            "记录不存在", "证明 Query 已正确绑定且非空（否则会是『搜索词不能为空』或模型绑定 400）");
    }

    private async Task CompleteSetupAsync()
    {
        await _client.PostAsJsonAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await _client.PostAsJsonAsync("/api/setup/complete", new { });
    }

    private async Task LoginAsAdminAsync()
    {
        await CompleteSetupAsync();
        HttpResponseMessage loginResp = await _client.PostAsJsonAsync(
            "/api/auth/login", new { username = "admin", password = "secret123" });
        JsonElement login = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
