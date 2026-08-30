using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>WatchIgnoreRules e2e：CRUD 闭环 + 默认种子可见 + 非法 Extension Pattern → 1000</summary>
public sealed class WatchIgnoreRulesTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public WatchIgnoreRulesTests()
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
    public async Task DefaultSeed_Returns16Extensions()
    {
        await LoginAsAdminAsync();

        JsonElement list = await GetAsync("/api/settings/watch/ignore-rules");
        list.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        int extCount = list.GetProperty("data").EnumerateArray()
            .Count(e => e.GetProperty("type").GetString() == "Extension");
        extCount.Should().Be(16, "Migration HasData 注入 16 条默认扩展名");
    }

    [Fact]
    public async Task Crud_RoundTrip()
    {
        await LoginAsAdminAsync();

        // Create Keyword
        JsonElement created = await PostAsync("/api/settings/watch/ignore-rules", new
        {
            type = "Keyword",
            pattern = "Sample",
            description = "样片关键词",
            enabled = true,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        created.GetProperty("data").GetProperty("pattern").GetString().Should().Be("sample"); // 小写归一化
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        // Update：改 enabled + description
        JsonElement upd = await PostAsync("/api/settings/watch/ignore-rules/update", new
        {
            id,
            type = "Keyword",
            pattern = "sample",
            description = "改后描述",
            enabled = false,
        });
        upd.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse();
        upd.GetProperty("data").GetProperty("description").GetString().Should().Be("改后描述");

        // Delete
        JsonElement del = await PostAsync("/api/settings/watch/ignore-rules/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task Create_ExtensionWithoutDot_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/watch/ignore-rules", new
        {
            type = "Extension",
            pattern = "aria2",
            description = (string?)null,
            enabled = true,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("'.' 开头");
    }

    [Fact]
    public async Task Create_DuplicatePattern_Returns1000()
    {
        await LoginAsAdminAsync();
        // .part 已在默认种子中
        JsonElement resp = await PostAsync("/api/settings/watch/ignore-rules", new
        {
            type = "Extension",
            pattern = ".part",
            description = "duplicate",
            enabled = true,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("已存在");
    }

    private async Task LoginAsAdminAsync()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<JsonElement> GetAsync(string url) =>
        await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> PostAsync(string url, object payload) =>
        await (await _client.PostAsJsonAsync(url, payload)).Content.ReadFromJsonAsync<JsonElement>();
}
