using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>CategoryMatchRules e2e：CRUD 闭环 + 非 JSON Conditions → 1000 + 未知 CategoryId → 1000 + categoryId 过滤</summary>
public sealed class CategoryMatchRulesTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public CategoryMatchRulesTests()
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
    public async Task Crud_RoundTrip_WithJsonConditions()
    {
        await LoginAsAdminAsync();
        long catId = await CreateCategoryAsync("电影");

        JsonElement created = await PostAsync("/api/settings/category-match-rules", new
        {
            categoryId = catId,
            name = "movie 默认",
            conditions = "{\"all\":[{\"field\":\"type\",\"op\":\"eq\",\"value\":\"movie\"}]}",
            priority = 100,
            enabled = true,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        created.GetProperty("data").GetProperty("conditions").GetString().Should().Contain("eq");

        JsonElement upd = await PostAsync("/api/settings/category-match-rules/update", new
        {
            id,
            categoryId = catId,
            name = "movie 默认 v2",
            conditions = "{\"any\":[]}",
            priority = 10,
            enabled = false,
        });
        upd.GetProperty("data").GetProperty("priority").GetInt32().Should().Be(10);
        upd.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse();

        JsonElement del = await PostAsync("/api/settings/category-match-rules/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task Create_InvalidConditionsJson_Returns1000()
    {
        await LoginAsAdminAsync();
        long catId = await CreateCategoryAsync("电影");

        JsonElement resp = await PostAsync("/api/settings/category-match-rules", new
        {
            categoryId = catId,
            name = "bad",
            conditions = "{ not json",
            priority = 100,
            enabled = true,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("JSON");
    }

    [Fact]
    public async Task Create_ObjectConditions_RejectedAsBusinessError()
    {
        await LoginAsAdminAsync();
        long catId = await CreateCategoryAsync("剧集");

        // 复现前端旧 bug：CategoryRuleBuilder 误把 conditions 当「对象」提交，
        // 但后端 DTO Conditions 是 string（JSON 文本）→ 模型绑定失败。
        // 档 2 边界校验接入后，模型绑定 / 校验失败统一走 ValidationEnvelopeFactory → 200 + code 1000，
        // 不再吐 400 ProblemDetails（符合 §0.6「错误走 200 + code」）；
        // 修复后前端改为 JSON.stringify 提交字符串（正例见 Crud_RoundTrip_WithJsonConditions）。
        JsonElement resp = await PostAsync("/api/settings/category-match-rules", new
        {
            categoryId = catId,
            name = "规则 #1780546485679",
            priority = 100,
            enabled = true,
            conditions = new { all = new[] { new { field = "type", op = "eq", value = "tv" } } },
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
    }

    [Fact]
    public async Task Create_UnknownCategoryId_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/category-match-rules", new
        {
            categoryId = 99999L,
            name = "x",
            conditions = "{}",
            priority = 100,
            enabled = true,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("分类");
    }

    [Fact]
    public async Task List_FilterByCategoryId()
    {
        await LoginAsAdminAsync();
        long catA = await CreateCategoryAsync("电影");
        long catB = await CreateCategoryAsync("剧集");
        await PostAsync("/api/settings/category-match-rules", new { categoryId = catA, name = "a1", conditions = "{}", priority = 100, enabled = true });
        await PostAsync("/api/settings/category-match-rules", new { categoryId = catA, name = "a2", conditions = "{}", priority = 200, enabled = true });
        await PostAsync("/api/settings/category-match-rules", new { categoryId = catB, name = "b1", conditions = "{}", priority = 100, enabled = true });

        JsonElement onlyA = await GetAsync($"/api/settings/category-match-rules?categoryId={catA}");
        onlyA.GetProperty("data").GetArrayLength().Should().Be(2);
        onlyA.GetProperty("data").EnumerateArray().All(e => e.GetProperty("categoryId").GetInt64() == catA).Should().BeTrue();
    }

    private async Task<long> CreateCategoryAsync(string name)
    {
        JsonElement cat = await PostAsync("/api/settings/categories", new
        {
            name,
            mediaType = "Movie",
            targetRoot = $"/plex/{name}",
        });
        return cat.GetProperty("data").GetProperty("id").GetInt64();
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
