using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Controllers;

/// <summary>r1 P1/P2 新端点烟雾测 — 7 端点 × (401 + Admin Code0) = 14 用例</summary>
/// <remarks>
/// 覆盖 4efa7d7 commit 落地的 7 个端点：
/// - POST /review/batch-ignore （Admin）
/// - POST /system/clear-history （Admin）
/// - POST /system/reset-config （Admin）
/// - GET  /history/{id}/nfo （Admin；不存在 id 命中 1000，验证鉴权通过）
/// - POST /settings/parse-rules/import （Admin，multipart）
/// - GET  /dashboard/heatmap （AllowAnonymous + Admin 双跑确认未误收紧）
/// - GET  /dashboard/watch-folder-activity （AllowAnonymous + Admin 双跑）
///
/// 不做端到端业务断言（已在 Service/Persistence 测试覆盖），仅验证：
/// 路由可达 + 鉴权规则 + ApiCode 包装格式。每用例独立 PmmHostFactory（SetupGuard 静态缓存防污染）。
/// </remarks>
public sealed class D9NewEndpointsTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public D9NewEndpointsTests()
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

    // ---------- 1. Review BatchIgnore ----------

    [Fact]
    public async Task Review_BatchIgnore_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync(
            "/api/review/batch-ignore",
            new { items = new object[0], reason = (string?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Review_BatchIgnore_With_Admin_NonExistent_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync(
            "/api/review/batch-ignore",
            new { items = new[] { new { id = 999_999L, rowVersion = 0L } }, reason = "测试样本" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        // 不存在的 id 进入 failed[]，外层仍 code=0（部分失败语义）
        body.GetProperty("data").GetProperty("succeeded").GetArrayLength().Should().Be(0);
        body.GetProperty("data").GetProperty("failed").GetArrayLength().Should().Be(1);
    }

    // ---------- 2. System ClearHistory ----------

    [Fact]
    public async Task System_ClearHistory_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/system/clear-history", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task System_ClearHistory_With_Admin_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/system/clear-history", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        // 干净 DB 下 6 项删除数都应为 0；字段必须存在
        body.GetProperty("data").GetProperty("mediaItems").GetInt32().Should().Be(0);
        body.GetProperty("data").GetProperty("webhookDeliveries").GetInt32().Should().Be(0);
    }

    // ---------- 3. System ResetConfig ----------

    [Fact]
    public async Task System_ResetConfig_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/system/reset-config", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task System_ResetConfig_With_Admin_Returns_200_AndCode0_AndReseededTrue()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.PostAsync("/api/system/reset-config", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("reseededDefaults").GetBoolean().Should().BeTrue(
            "ResetConfig 必须调用 DataSeeder 重种默认分类与解析规则");
    }

    // ---------- 4. History Nfo ----------

    [Fact]
    public async Task History_Nfo_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/history/1/nfo");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>不存在 id 命中 1000「记录不存在」；本测试验证鉴权管道放行了 Admin，不验证业务成功路径（需真实归档数据）</summary>
    [Fact]
    public async Task History_Nfo_With_Admin_NonExistent_Returns_200_AndBusinessError()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/history/999999/nfo");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        doc.RootElement.TryGetProperty("requestId", out _).Should().BeTrue();
    }

    // ---------- 5. ParseRules Import ----------

    [Fact]
    public async Task ParseRules_Import_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("[]")), "file", "rules.json");
        HttpResponseMessage resp = await _client.PostAsync("/api/settings/parse-rules/import?mode=Merge", form);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ParseRules_Import_With_Admin_ValidJson_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        // 一条合法 + 一条与种子规则同名（应被 Merge 模式 skipped）的最小数组
        const string payload = """
            [
              {
                "name": "导入测试-唯一规则",
                "scope": "FileName",
                "pattern": "^(?<title>.+?)\\.(?<year>\\d{4})\\.",
                "defaultType": "movie",
                "forceType": false,
                "priority": 200,
                "confidenceBonus": 0.05,
                "enabled": true,
                "description": null
              }
            ]
            """;
        using MultipartFormDataContent form = new();
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(payload));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(fileContent, "file", "rules.json");

        HttpResponseMessage resp = await _client.PostAsync("/api/settings/parse-rules/import?mode=Merge", form);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").TryGetProperty("added", out _).Should().BeTrue();
        body.GetProperty("data").TryGetProperty("skipped", out _).Should().BeTrue();
        body.GetProperty("data").TryGetProperty("failures", out _).Should().BeTrue();
        (body.GetProperty("data").GetProperty("added").GetInt32()
         + body.GetProperty("data").GetProperty("skipped").GetInt32())
            .Should().Be(1, "传入一条合法规则，必为 added 或 skipped 之一");
    }

    // ---------- 6. Dashboard Heatmap （AllowAnonymous）----------

    [Fact]
    public async Task Dashboard_Heatmap_Anonymous_Returns_200_AndCode0()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/dashboard/heatmap");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("grid").GetArrayLength().Should().Be(7, "7 行 × 24 列网格（按 DayOfWeek 排）");
    }

    [Fact]
    public async Task Dashboard_Heatmap_With_Admin_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/dashboard/heatmap?from=2026-05-01T00:00:00Z&to=2026-05-25T00:00:00Z");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("grid").GetArrayLength().Should().Be(7);
    }

    // ---------- 7. Dashboard WatchFolderActivity （AllowAnonymous）----------

    [Fact]
    public async Task Dashboard_WatchFolderActivity_Anonymous_Returns_200_AndCode0()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/dashboard/watch-folder-activity");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Dashboard_WatchFolderActivity_With_Admin_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/dashboard/watch-folder-activity");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ---------- helpers ----------

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

    private static async Task<JsonElement> AssertCode0(HttpResponseMessage resp)
    {
        string raw = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        doc.RootElement.TryGetProperty("requestId", out _).Should().BeTrue();
        return doc.RootElement.Clone();
    }
}
