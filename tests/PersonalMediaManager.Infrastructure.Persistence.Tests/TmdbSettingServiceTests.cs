using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Tmdb;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>TmdbSettingService：语言变更自动清空 TMDB 缓存（零迁移退路方案回归）</summary>
/// <remarks>
/// 背景：Tmdb_MetadataCache 键 (TmdbId, MediaType) 不含语言维度，改语言后旧语言条目会继续命中至 TTL
/// 过期（默认 24h）。给键加语言列涉及迁移（红线），故 UpdateAsync 检测 Language/FallbackLanguage 变更
/// 后调用清缓存能力清空两表；本测试守护该行为及「未变更不清」「大小写差异不算变更」。
/// </remarks>
public sealed class TmdbSettingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly TmdbSettingService _sut;

    public TmdbSettingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.Database.EnsureCreated(); // Tmdb_Setting Id=1 种子（zh-CN / en-US）由 HasData 注入
        }

        _sut = new TmdbSettingService(
            _dbFactory,
            Substitute.For<IProtectedFieldService>(),
            Substitute.For<ITmdbConnectivityTester>(),
            NullLogger<TmdbSettingService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>预置两表缓存行（搜索 1 条 + 元数据 1 条）</summary>
    private void SeedCaches()
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.TmdbSearchCaches.Add(new TmdbSearchCache
        {
            QueryHash = new string('a', 64),
            QueryRaw = "tv:某剧(2023)[zh-CN]",
            Results = "[]",
            CachedAt = DateTimeOffset.UtcNow,
        });
        ctx.TmdbMetadataCaches.Add(new TmdbMetadataCache
        {
            TmdbId = 100,
            MediaType = "tv",
            Title = "旧语言标题",
            RawJson = "{}",
            CachedAt = DateTimeOffset.UtcNow,
        });
        ctx.SaveChanges();
    }

    private (int Search, int Metadata) CountCaches()
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return (ctx.TmdbSearchCaches.Count(), ctx.TmdbMetadataCaches.Count());
    }

    /// <summary>构造合法更新请求（默认与种子一致：zh-CN / en-US，权重和=1.0）</summary>
    private static UpdateTmdbSettingRequest BuildRequest(
        string language = "zh-CN",
        string fallbackLanguage = "en-US",
        int rateLimitPerSecond = 40) =>
        new(ApiKey: null,
            Language: language,
            FallbackLanguage: fallbackLanguage,
            CandidateThreshold: 3,
            RateLimitPerSecond: rateLimitPerSecond,
            MetadataCacheHours: 24,
            SearchCacheMinutes: 60,
            ScoreWeightTitle: 0.5,
            ScoreWeightYear: 0.3,
            ScoreWeightPopularity: 0.1,
            ScoreWeightLanguage: 0.1);

    [Fact(DisplayName = "主语言变更 → 自动清空两张 TMDB 缓存表")]
    public async Task Update_LanguageChanged_ClearsBothCaches()
    {
        SeedCaches();
        CountCaches().Should().Be((1, 1));

        TmdbSettingResponse resp = await _sut.UpdateAsync(BuildRequest(language: "en-US"));

        resp.Language.Should().Be("en-US");
        CountCaches().Should().Be((0, 0), "改语言后旧语言缓存（尤其元数据表键不含语言）必须立即失效");
    }

    [Fact(DisplayName = "回退语言变更 → 同样清空缓存")]
    public async Task Update_FallbackLanguageChanged_ClearsBothCaches()
    {
        SeedCaches();

        await _sut.UpdateAsync(BuildRequest(fallbackLanguage: "ja-JP"));

        CountCaches().Should().Be((0, 0));
    }

    [Fact(DisplayName = "语言未变（仅改其它字段）→ 缓存保留")]
    public async Task Update_LanguageUnchanged_KeepsCaches()
    {
        SeedCaches();

        TmdbSettingResponse resp = await _sut.UpdateAsync(BuildRequest(rateLimitPerSecond: 20));

        resp.RateLimitPerSecond.Should().Be(20);
        CountCaches().Should().Be((1, 1), "语言没变不应清缓存");
    }

    [Fact(DisplayName = "仅大小写差异（zh-cn）→ 不算语言变更，缓存保留")]
    public async Task Update_LanguageCaseOnlyDifference_KeepsCaches()
    {
        SeedCaches();

        await _sut.UpdateAsync(BuildRequest(language: "zh-cn"));

        CountCaches().Should().Be((1, 1), "TMDB 语言标签大小写不敏感，纯大小写差异不应清缓存");
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }
}
