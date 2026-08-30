using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Controllers;

/// <summary>字幕端点冒烟 — 路由可达 + 鉴权（401/403）+ 校验信封 + 守门 1000</summary>
/// <remarks>
/// 覆盖 212d3ba commit 落地的字幕端点（业务逻辑已由 Persistence/External 单测覆盖，此处只守 WebApplicationFactory 层）：
/// - GET  /settings/subtitle         （匿名 401 / Viewer 403 / Admin 种子单例 hasToken=false）
/// - POST /settings/subtitle/update  （合法回写落库 / TimeoutSeconds 越界 [5,120] 走校验信封 1000）
/// - GET  /media/{id}/subtitles/search（媒体不存在 1000 / 未归档 1000 —— 守门先拒，不触外部源）
/// - POST /media/{id}/subtitles/download（subtitleId 空白走校验信封 1000，模型绑定层拦截不进 Action）
///
/// 刻意不 stub ISubtitleProviderRegistry：所有用例都终止在鉴权 / 模型校验 / 媒体守门，
/// 走不到 Assrt 外部调用（SearchAsync 先查媒体记录再查 Token，见 SubtitleDownloadService.LoadMediaOrThrowAsync）。
/// 每用例独立 PmmHostFactory（SetupGuard 静态缓存防污染）。
/// </remarks>
public sealed class SubtitleEndpointsSmokeTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public SubtitleEndpointsSmokeTests()
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

    // ---------- 1. GET /settings/subtitle ----------

    [Fact]
    public async Task SettingsGet_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/settings/subtitle");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>全局 FallbackPolicy = 已认证 + RequireRole(Admin)：Viewer 已认证但角色不符 → 403</summary>
    [Fact]
    public async Task SettingsGet_With_Viewer_Returns_403()
    {
        await LoginAsViewerAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/settings/subtitle");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SettingsGet_With_Admin_Returns_SeedSingleton()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/settings/subtitle");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await AssertCode0(resp);
        body.GetProperty("data").GetProperty("hasToken").GetBoolean().Should().BeFalse("种子未配置 Assrt Token");
        body.GetProperty("data").GetProperty("baseUrl").GetString().Should().Be("https://api.assrt.net");
        body.GetProperty("data").GetProperty("timeoutSeconds").GetInt32().Should().Be(30);
        body.GetProperty("data").GetProperty("useProxy").GetBoolean().Should().BeFalse();
    }

    // ---------- 2. POST /settings/subtitle/update ----------

    [Fact]
    public async Task SettingsUpdate_With_Admin_ValidBody_PersistsAndEchoes()
    {
        await LoginAsAdminAsync();
        JsonElement upd = await PostAsync("/api/settings/subtitle/update", new
        {
            token = (string?)null, // null = Token 不变（保持未配置）
            baseUrl = "https://api.assrt.net",
            timeoutSeconds = 60,
            useProxy = true,
        });
        upd.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        upd.GetProperty("data").GetProperty("timeoutSeconds").GetInt32().Should().Be(60);
        upd.GetProperty("data").GetProperty("useProxy").GetBoolean().Should().BeTrue();
        upd.GetProperty("data").GetProperty("hasToken").GetBoolean().Should().BeFalse("token=null 不应被当作配置了 Token");

        // 再 GET 确认确已落库（非仅响应回显）
        JsonElement got = await GetAsync("/api/settings/subtitle");
        got.GetProperty("data").GetProperty("timeoutSeconds").GetInt32().Should().Be(60);
        got.GetProperty("data").GetProperty("useProxy").GetBoolean().Should().BeTrue();
    }

    /// <summary>TimeoutSeconds 越界 [Range(5,120)] → 模型绑定校验信封：HTTP 200 + code=1000 + data 字段清单</summary>
    [Fact]
    public async Task SettingsUpdate_TimeoutOutOfRange_ReturnsValidationEnvelope()
    {
        await LoginAsAdminAsync();
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/settings/subtitle/update", new
        {
            token = (string?)null,
            baseUrl = "https://api.assrt.net",
            timeoutSeconds = 300, // 超出 [5, 120]
            useProxy = false,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "校验失败按 §0.6 走 200 + code，而非 400 ProblemDetails");
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Contain("超时秒数");
        AssertHasFieldError(body, "timeoutSeconds");
    }

    // ---------- 3. GET /media/{id}/subtitles/search ----------

    [Fact]
    public async Task Search_Without_Auth_Returns_401()
    {
        await CompleteSetupAsync();
        HttpResponseMessage resp = await _client.GetAsync("/api/media/1/subtitles/search");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_With_Admin_NonExistentMedia_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await GetAsync("/api/media/999999/subtitles/search");
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("媒体记录不存在");
    }

    /// <summary>Detected（未归档）记录命中「仅已归档完成」守门；守门先于 Token 校验与外部源调用</summary>
    [Fact]
    public async Task Search_With_Admin_UnarchivedMedia_Returns1000()
    {
        await LoginAsAdminAsync();
        long mediaId = await SeedDetectedMediaAsync();

        JsonElement resp = await GetAsync($"/api/media/{mediaId}/subtitles/search");
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("仅已归档完成的记录可下载字幕");
    }

    // ---------- 4. POST /media/{id}/subtitles/download ----------

    /// <summary>subtitleId 空白 → [RequiredNotBlank] 在模型绑定层拦截（校验信封），不进 Action 也不触外部源</summary>
    [Fact]
    public async Task Download_With_Admin_BlankSubtitleId_ReturnsValidationEnvelope()
    {
        await LoginAsAdminAsync();
        // 媒体 id 随意：模型校验先于 Action 执行，媒体存在性根本不会被查
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/media/999999/subtitles/download", new
        {
            subtitleId = "",
            fileIndex = 0,
            subtitleName = (string?)null,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        body.GetProperty("message").GetString().Should().Contain("字幕 Id 不能为空");
        AssertHasFieldError(body, "subtitleId");
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
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Admin 经 /account/users/create 建 Viewer（走产品路径），再切换为 Viewer 登录态</summary>
    private async Task LoginAsViewerAsync()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/account/users/create",
            new { username = "viewer", password = "viewer123", role = "Viewer" });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success, "前置：Viewer 账号必须创建成功");

        JsonElement login = await PostAsync("/api/auth/login", new { username = "viewer", password = "viewer123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>直插一条 Detected 状态媒体记录（未归档、无 TargetPath），用于命中归档守门</summary>
    private async Task<long> SeedDetectedMediaAsync()
    {
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext ctx = await dbFactory.CreateDbContextAsync();
        MediaItem item = MediaItem.CreateDetected(@"C:\watch\示例剧集.S01E01.1080p.mkv", "示例剧集.S01E01.1080p.mkv", 1024);
        ctx.MediaItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<JsonElement> AssertCode0(HttpResponseMessage resp)
    {
        string raw = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        doc.RootElement.TryGetProperty("requestId", out _).Should().BeTrue();
        return doc.RootElement.Clone();
    }

    /// <summary>断言校验信封 data 为字段错误数组且含指定字段（ValidationEnvelopeFactory 已统一小驼峰）</summary>
    private static void AssertHasFieldError(JsonElement body, string field)
    {
        body.TryGetProperty("requestId", out _).Should().BeTrue();
        JsonElement data = body.GetProperty("data");
        data.ValueKind.Should().Be(JsonValueKind.Array);
        data.EnumerateArray().Any(f => f.GetProperty("field").GetString() == field)
            .Should().BeTrue($"校验信封 data 应含字段 {field} 的错误项");
    }

    private async Task<JsonElement> GetAsync(string url) =>
        await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> PostAsync(string url, object payload) =>
        await (await _client.PostAsJsonAsync(url, payload)).Content.ReadFromJsonAsync<JsonElement>();
}
