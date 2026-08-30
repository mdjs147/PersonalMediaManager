using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>Categories e2e：CRUD 闭环 + 重名 1000 + 删除前 Media_Item 引用守护 + MatchRule CASCADE</summary>
public sealed class CategoriesTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public CategoriesTests()
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
    public async Task Crud_RoundTrip()
    {
        await LoginAsAdminAsync();

        JsonElement created = await PostAsync("/api/settings/categories", new
        {
            name = "电影",
            mediaType = "Movie",
            targetRoot = "D:/Plex/Movies",
            priority = 100,
            description = (string?)null,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);

        JsonElement list = await GetAsync("/api/settings/categories");
        list.GetProperty("data").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt64() == id).Should().BeTrue();

        JsonElement upd = await PostAsync("/api/settings/categories/update", new
        {
            id,
            name = "电影改",
            mediaType = "Movie",
            targetRoot = "D:/Plex/Movies2",
            priority = 50,
            description = "改后",
        });
        upd.GetProperty("data").GetProperty("name").GetString().Should().Be("电影改");
        upd.GetProperty("data").GetProperty("priority").GetInt32().Should().Be(50);

        JsonElement del = await PostAsync("/api/settings/categories/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns1000()
    {
        await LoginAsAdminAsync();
        await PostAsync("/api/settings/categories", new { name = "电影", mediaType = "Movie", targetRoot = "/a" });

        JsonElement dup = await PostAsync("/api/settings/categories", new { name = "电影", mediaType = "Tv", targetRoot = "/b" });
        dup.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        dup.GetProperty("message").GetString().Should().Contain("已存在");
    }

    [Fact]
    public async Task Delete_WithMediaItemReference_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/categories", new
        {
            name = "动漫",
            mediaType = "Tv",
            targetRoot = "/anime",
        });
        long catId = created.GetProperty("data").GetProperty("id").GetInt64();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            MediaItem item = MediaItem.CreateFixture("/data/x.mkv", "x.mkv", 1, categoryId: catId);
            ctx.MediaItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        JsonElement del = await PostAsync("/api/settings/categories/delete", new { id = catId });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        del.GetProperty("message").GetString().Should().Contain("引用");
    }

    [Fact]
    public async Task DeleteCategory_CascadesMatchRules()
    {
        await LoginAsAdminAsync();
        JsonElement cat = await PostAsync("/api/settings/categories", new
        {
            name = "纪录片",
            mediaType = "Both",
            targetRoot = "/docs",
        });
        long catId = cat.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement rule = await PostAsync("/api/settings/category-match-rules", new
        {
            categoryId = catId,
            name = "默认",
            conditions = "{}",
            priority = 100,
            enabled = true,
        });
        long ruleId = rule.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement del = await PostAsync("/api/settings/categories/delete", new { id = catId });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        // 验证 CASCADE：匹配规则也应被删
        JsonElement listAll = await GetAsync("/api/settings/category-match-rules");
        listAll.GetProperty("data").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt64() == ruleId).Should().BeFalse("CategoryId CASCADE 应连同清规则");
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
