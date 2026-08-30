using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests.Performance;

/// <summary>
/// F.2 性能基线（dev plan §F.2）— TMDB 缓存命中下重复处理 &lt; 500ms
///
/// 本测试只盖「缓存命中」一条性能基线（其余 3 条指标涉及真实 IO / 端到端 / 大批量扫描，
/// 写进 F.4 跨平台手工冒烟清单 + F.1 真服务 E2E，由用户介入）。
///
/// 测量方法：
/// 1. 用 SQLite in-memory 装 PmmDbContext + 写入 Tmdb_Setting 种子
/// 2. Mock ITmdbClient.SearchAsync 一次（确保第一次预热写入缓存）
/// 3. 预热一次（不计时；触发缓存写入）
/// 4. 计时连续 5 次相同查询；每次 elapsed &lt; 500ms（在低端 CI agent 上也应远低于 100ms）
/// 5. 断言 5 次平均 &lt; 100ms（in-memory SQLite + 缓存命中通常 &lt; 10ms）
/// 6. 断言 ITmdbClient.SearchAsync 总共仅被调 1 次（首次预热）
/// </summary>
public sealed class F2TmdbCacheHitPerfTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ITmdbClient _client;
    private readonly IProtectedFieldService _protector;
    private readonly TmdbSearchService _sut;

    public F2TmdbCacheHitPerfTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
            // Tmdb_Setting Id=1 由 HasData 种子注入；这里只补 ApiKey（生产是用户在 Setup 时填）
            TmdbSetting setting = ctx.TmdbSettings.Find(1L)
                ?? throw new InvalidOperationException("EnsureCreated 后 Tmdb_Setting 种子未就位");
            setting.ApiKeyEncrypted = "ENCRYPTED_KEY_PLACEHOLDER";
            setting.SearchCacheMinutes = 60;
            setting.MetadataCacheHours = 24;
            ctx.SaveChanges();
        }

        _client = Substitute.For<ITmdbClient>();
        _protector = Substitute.For<IProtectedFieldService>();
        _protector.Unprotect(Arg.Any<string>()).Returns("FAKE_API_KEY_RUNTIME_VALUE");

        TmdbSearchResult fakeResult = new(
            Candidates: new List<TmdbCandidate>
            {
                new(1, "movie", "Inception 中文", "Inception", 2010, 80.5, "en",
                    new List<string> { "US" }, "/poster.jpg", "梦境与现实"),
            },
            RawJson: "{\"results\":[]}");
        _client.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(fakeResult);

        _sut = new TmdbSearchService(_dbFactory, _protector, _client,
            Substitute.For<IPosterDownloader>(),
            AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-tmdb-{Guid.NewGuid():N}")),
            NullLogger<TmdbSearchService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "F.2 TMDB 缓存命中：连续 5 次重复查询每次 < 500ms + 总命中底层 client 仅 1 次")]
    public async Task CacheHit_RepeatedQuery_StaysUnder500msAndHitsClientOnce()
    {
        TmdbSearchRequest req = new("Inception", "movie", Year: 2010);

        // 1. 预热（首次走 client + 写缓存；不计时）
        TmdbSearchResult warmup = await _sut.SearchAsync(req);
        warmup.Candidates.Should().HaveCount(1, "fake client 返 1 个候选");

        // 2. 连续 5 次缓存命中，每次计时
        const int rounds = 5;
        long[] elapsedMs = new long[rounds];
        for (int i = 0; i < rounds; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            TmdbSearchResult cached = await _sut.SearchAsync(req);
            sw.Stop();
            cached.Candidates.Should().HaveCount(1);
            elapsedMs[i] = sw.ElapsedMilliseconds;
        }

        // 3. 单次必须 < 500ms（dev plan §F.2 性能目标）
        foreach (long ms in elapsedMs)
        {
            ms.Should().BeLessThan(500, "TMDB 缓存命中性能基线（dev plan §F.2）");
        }

        // 4. 平均 < 100ms（in-memory SQLite + 缓存命中典型表现）
        double avg = elapsedMs.Sum() / (double)rounds;
        avg.Should().BeLessThan(100, "5 次平均不应超过 100ms（in-memory SQLite + 缓存命中）");

        // 5. 底层 ITmdbClient 总共仅被调 1 次（预热那次；其余 5 次全走缓存）
        await _client.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "F.2 元数据缓存命中：连续 5 次 GetDetailsAsync 每次 < 500ms + client 仅 1 次")]
    public async Task MetadataCacheHit_RepeatedDetails_StaysUnder500msAndHitsClientOnce()
    {
        TmdbDetailsResult fakeDetails = new(
            TmdbId: 27205,
            MediaType: "movie",
            Title: "盗梦空间",
            OriginalTitle: "Inception",
            Year: 2010,
            TotalSeasons: null,
            PosterPath: "/poster.jpg",
            OriginCountry: new List<string> { "US" },
            OriginalLanguage: "en",
            GenresJson: "[]",
            Overview: "梦境与现实",
            RawJson: "{}");
        _client.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(fakeDetails);

        // 预热
        TmdbDetailsResult warmup = await _sut.GetDetailsAsync(27205, "movie");
        warmup.Title.Should().Be("盗梦空间");

        const int rounds = 5;
        long[] elapsedMs = new long[rounds];
        for (int i = 0; i < rounds; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            TmdbDetailsResult cached = await _sut.GetDetailsAsync(27205, "movie");
            sw.Stop();
            cached.Title.Should().Be("盗梦空间");
            elapsedMs[i] = sw.ElapsedMilliseconds;
        }

        foreach (long ms in elapsedMs)
        {
            ms.Should().BeLessThan(500, "TMDB 元数据缓存命中性能基线");
        }

        await _client.Received(1).GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
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
