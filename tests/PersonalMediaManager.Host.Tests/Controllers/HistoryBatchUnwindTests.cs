using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Controllers;

/// <summary>批量退回 / 批量撤销归档端点烟雾测 — 2 端点 × 鉴权 + 批量部分失败语义</summary>
/// <remarks>
/// 覆盖归档解除的两个批量端点（复用单条 Reopen/Undo 逐条执行）：
/// - POST /history/batch-reopen （Admin）
/// - POST /history/batch-undo-archive （Admin）
/// 仅验证路由可达 + 鉴权规则 + 批量「部分失败也 code=0、失败明细进 failed[]」语义 + 空请求守门；
/// 业务正确性（文件移回 / 删副本 / 状态回退）已由单条 Reopen/Undo 与 Persistence 测试覆盖，此处不重复。
/// 不存在的 id 进 failed[]、succeeded 为空，外层仍 code=0（与 review/batch-ignore 同口径）。
/// </remarks>
public sealed class HistoryBatchUnwindTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public HistoryBatchUnwindTests()
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

    // ---------- batch-reopen ----------

    [Fact]
    public async Task History_BatchReopen_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync(
            "/api/history/batch-reopen", new { ids = new[] { 1L } });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task History_BatchReopen_With_Admin_NonExistent_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync(
            "/api/history/batch-reopen", new { ids = new[] { 999_999L } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        // 不存在的 id 进 failed[]，外层仍 code=0（部分失败语义）
        body.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("data").GetProperty("succeeded").GetArrayLength().Should().Be(0);
        body.GetProperty("data").GetProperty("failed").GetArrayLength().Should().Be(1);
    }

    // ---------- batch-undo-archive ----------

    [Fact]
    public async Task History_BatchUndoArchive_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync(
            "/api/history/batch-undo-archive", new { ids = new[] { 1L } });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task History_BatchUndoArchive_With_Admin_NonExistent_Returns_200_AndCode0()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync(
            "/api/history/batch-undo-archive", new { ids = new[] { 999_999L } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("data").GetProperty("succeeded").GetArrayLength().Should().Be(0);
        body.GetProperty("data").GetProperty("failed").GetArrayLength().Should().Be(1);
    }

    // ---------- 空请求守门（既无 ids 也无 tmdbId）----------

    [Fact]
    public async Task History_BatchReopen_With_Admin_EmptyRequest_Returns_BusinessError()
    {
        await LoginAsAdminAsync();
        // ResolveTargetIdsAsync 两者皆空 → BusinessException「未指定要操作的记录」→ 全局中间件包成 200 + code=1000
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/history/batch-reopen", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        doc.RootElement.TryGetProperty("requestId", out _).Should().BeTrue();
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
