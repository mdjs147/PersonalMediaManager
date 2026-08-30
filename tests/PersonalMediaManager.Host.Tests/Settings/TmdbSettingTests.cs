using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>TmdbSetting e2e：Get 单例 / Update + ApiKey 加密 / 无 ApiKey /test → 1000 / 权重和 ≠ 1 → 1000 / /cache/clear</summary>
public sealed class TmdbSettingTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public TmdbSettingTests()
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
    public async Task Get_ReturnsSeedSingleton()
    {
        await LoginAsAdminAsync();

        JsonElement resp = await GetAsync("/api/settings/tmdb");
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        resp.GetProperty("data").GetProperty("hasApiKey").GetBoolean().Should().BeFalse("种子未设置 ApiKey");
        resp.GetProperty("data").GetProperty("language").GetString().Should().Be("zh-CN");
        resp.GetProperty("data").GetProperty("candidateThreshold").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Update_StoresApiKeyEncryptedAndDoesNotEcho()
    {
        await LoginAsAdminAsync();
        const string apiKey = "v4-bearer-tmdb-secret";

        JsonElement upd = await PostAsync("/api/settings/tmdb/update", new
        {
            apiKey,
            language = "zh-CN",
            fallbackLanguage = "en-US",
            candidateThreshold = 5,
            rateLimitPerSecond = 30,
            metadataCacheHours = 12,
            searchCacheMinutes = 30,
            scoreWeightTitle = 0.5,
            scoreWeightYear = 0.3,
            scoreWeightPopularity = 0.1,
            scoreWeightLanguage = 0.1,
        });
        upd.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        upd.GetProperty("data").GetProperty("hasApiKey").GetBoolean().Should().BeTrue();
        upd.GetRawText().Should().NotContain(apiKey, "ApiKey 明文不能出现在响应里");

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            TmdbSetting e = await ctx.TmdbSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
            e.ApiKeyEncrypted.Should().NotBeNullOrEmpty();
            e.ApiKeyEncrypted.Should().NotBe(apiKey, "DB 列必须是密文不是明文");
            e.CandidateThreshold.Should().Be(5);
        }
    }

    [Fact]
    public async Task Update_WeightsSumNotOne_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/tmdb/update", new
        {
            apiKey = (string?)null,
            language = "zh-CN",
            fallbackLanguage = "en-US",
            candidateThreshold = 3,
            rateLimitPerSecond = 40,
            metadataCacheHours = 24,
            searchCacheMinutes = 60,
            scoreWeightTitle = 0.5,
            scoreWeightYear = 0.3,
            scoreWeightPopularity = 0.5, // 和 = 1.4
            scoreWeightLanguage = 0.1,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("≈ 1.0");
    }

    [Fact]
    public async Task Test_WithoutApiKey_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/tmdb/test", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("ApiKey");
    }

    [Fact]
    public async Task ClearCache_ReturnsDeletionCounts()
    {
        await LoginAsAdminAsync();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ctx.TmdbSearchCaches.Add(new TmdbSearchCache
            {
                QueryHash = "h1",
                QueryRaw = "inception",
                Results = "[]",
                CachedAt = DateTimeOffset.UtcNow,
            });
            ctx.TmdbMetadataCaches.Add(new TmdbMetadataCache
            {
                TmdbId = 27205,
                MediaType = "movie",
                Title = "Inception",
                CachedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        JsonElement resp = await PostAsync("/api/settings/tmdb/cache/clear", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        resp.GetProperty("data").GetProperty("searchRowsDeleted").GetInt32().Should().Be(1);
        resp.GetProperty("data").GetProperty("metadataRowsDeleted").GetInt32().Should().Be(1);
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
