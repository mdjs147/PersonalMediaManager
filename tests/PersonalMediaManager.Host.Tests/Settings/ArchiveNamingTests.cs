using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>归档命名模板：get / update / preview 端点闭环（含校验拒绝 + 预览样例）</summary>
public sealed class ArchiveNamingTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public ArchiveNamingTests()
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
    public async Task GetUpdatePreview_RoundTrip()
    {
        await LoginAdminAsync();

        // 1. 读默认（种子）
        JsonElement get1 = await GetAsync("/api/settings/archive-naming");
        get1.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        get1.GetProperty("data").GetProperty("movieNameTemplate").GetString().Should().Be("{title} ({year})");
        get1.GetProperty("data").GetProperty("seasonFolderIncludeTitle").GetBoolean().Should().BeTrue();

        // 2. 更新为自定义（Emby 风单集去 ' - '）+ 关季标题
        JsonElement upd = await PostAsync("/api/settings/archive-naming/update", new
        {
            movieNameTemplate = "{title} ({year})",
            tvShowFolderTemplate = "{originaltitle} ({year})",
            tvEpisodeNameTemplate = "{title} ({year}) {episode}",
            seasonFolderIncludeTitle = false,
        });
        upd.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        // 3. 回读反映更新
        JsonElement get2 = await GetAsync("/api/settings/archive-naming");
        get2.GetProperty("data").GetProperty("tvShowFolderTemplate").GetString().Should().Be("{originaltitle} ({year})");
        get2.GetProperty("data").GetProperty("seasonFolderIncludeTitle").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Preview_DefaultTemplates_ReturnsSamplesWithLockedStructure()
    {
        await LoginAdminAsync();
        JsonElement r = await PostAsync("/api/settings/archive-naming/preview", new
        {
            movieNameTemplate = "{title} ({year})",
            tvShowFolderTemplate = "{title} ({year})",
            tvEpisodeNameTemplate = "{title} ({year}) - {episode}",
            seasonFolderIncludeTitle = true,
        });
        r.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement data = r.GetProperty("data");
        data.GetProperty("movieErrors").GetArrayLength().Should().Be(0);

        // 样例里电影路径含 {tmdb-27205}；单集含 S03E01 + Season 03 + {tmdb-85937}
        string allSamples = string.Join("\n", data.GetProperty("samples").EnumerateArray()
            .Select(s => s.GetProperty("relativePath").GetString() ?? ""));
        allSamples.Should().Contain("{tmdb-27205}");
        allSamples.Should().Contain("S03E01");
        allSamples.Should().Contain("Season 03");
    }

    [Fact]
    public async Task Update_EpisodeTemplateMissingEpisodeToken_Returns1000()
    {
        await LoginAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/archive-naming/update", new
        {
            movieNameTemplate = "{title} ({year})",
            tvShowFolderTemplate = "{title} ({year})",
            tvEpisodeNameTemplate = "{title} ({year})", // 缺 {episode}
            seasonFolderIncludeTitle = true,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("{episode}");
    }

    [Fact]
    public async Task Preview_IllegalTemplate_ReturnsErrorList_NotThrow()
    {
        await LoginAdminAsync();
        JsonElement r = await PostAsync("/api/settings/archive-naming/preview", new
        {
            movieNameTemplate = "{title} ({year}) {tmdb-1}", // 含 {tmdb 字面量
            tvShowFolderTemplate = "{title} ({year})",
            tvEpisodeNameTemplate = "{title} ({year}) - {episode}",
            seasonFolderIncludeTitle = true,
        });
        r.GetProperty("code").GetInt32().Should().Be(ApiCode.Success, "预览不抛，错误走清单");
        r.GetProperty("data").GetProperty("movieErrors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ArchiveNaming_NotInGeneralSettingsList_Protected()
    {
        await LoginAdminAsync();
        // 归档命名 4 个 key 受保护，不出现在常规设置列表（防裸 KV 绕过模板校验）
        JsonElement list = await GetAsync("/api/settings/general");
        bool anyTemplateKey = list.GetProperty("data").GetProperty("groups").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray())
            .Any(e => e.GetProperty("key").GetString()!.StartsWith("Archive_") &&
                      e.GetProperty("key").GetString()!.Contains("Template"));
        anyTemplateKey.Should().BeFalse();
    }

    private async Task LoginAdminAsync()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("data").GetProperty("token").GetString());
    }

    private async Task<JsonElement> GetAsync(string url) =>
        await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> PostAsync(string url, object payload) =>
        await (await _client.PostAsJsonAsync(url, payload)).Content.ReadFromJsonAsync<JsonElement>();
}
