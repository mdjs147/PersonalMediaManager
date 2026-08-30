using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>TmdbSearchService 透明回退链 + 语言注入 + 速率注入（TMDB 集成组缺陷修复回归）</summary>
/// <remarks>
/// 覆盖：
/// 1. 带年零结果 → 自动去年份重搜（跨年播出剧 / AI 年份偏 1 不再误入人工审核）；
/// 2. 主语言全零 → FallbackLanguage 真实回退（旧版只进缓存键从不回退）；
/// 3. 请求未显式指定语言 → 注入 Tmdb_Setting.Language（旧版永远落 record 默认 zh-CN）；
/// 4. 回退链各层缓存键含 year+language → 重复执行全链走缓存，互不污染；
/// 5. Tmdb_Setting.RateLimitPerSecond 随调用流入 ITmdbClient。
/// </remarks>
public sealed class TmdbSearchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ITmdbClient _client;
    private readonly TmdbSearchService _sut;
    private readonly List<TmdbSearchRequest> _seenRequests = [];
    private readonly List<int?> _seenRates = [];

    public TmdbSearchServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
            TmdbSetting setting = ctx.TmdbSettings.Find(1L)
                ?? throw new InvalidOperationException("EnsureCreated 后 Tmdb_Setting 种子未就位");
            setting.ApiKeyEncrypted = "ENCRYPTED_KEY_PLACEHOLDER";
            ctx.SaveChanges();
        }

        _client = Substitute.For<ITmdbClient>();
        IProtectedFieldService protector = Substitute.For<IProtectedFieldService>();
        protector.Unprotect(Arg.Any<string>()).Returns("FAKE_API_KEY");

        _sut = new TmdbSearchService(_dbFactory, protector, _client,
            Substitute.For<IPosterDownloader>(),
            AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-tmdbsearch-{Guid.NewGuid():N}")),
            NullLogger<TmdbSearchService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>配置 mock：按谓词决定每次搜索返回候选数，并记录请求与速率参数</summary>
    private void SetupClient(Func<TmdbSearchRequest, int> candidateCountFor)
    {
        _client.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                TmdbSearchRequest req = ci.Arg<TmdbSearchRequest>();
                _seenRequests.Add(req);
                _seenRates.Add(ci.ArgAt<int?>(2));
                return BuildResult(candidateCountFor(req));
            });
    }

    private static TmdbSearchResult BuildResult(int count)
    {
        List<TmdbCandidate> candidates = [];
        for (int i = 1; i <= count; i++)
            candidates.Add(new TmdbCandidate(i, "tv", $"候选{i}", $"Candidate {i}", 2023, 10 * i, "zh", ["CN"], null, null));
        return new TmdbSearchResult(candidates, "{\"results\":[]}");
    }

    private void UpdateSetting(Action<TmdbSetting> mutate)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        TmdbSetting setting = ctx.TmdbSettings.Find(1L)!;
        mutate(setting);
        ctx.SaveChanges();
    }

    // ── 修复 1：年份精确过滤零结果 → 透明去年份重搜 ──

    [Fact(DisplayName = "带年零结果 + 去年份有结果 → 自动回退返回结果")]
    public async Task Search_YearMissesButNoYearHits_ReturnsFallbackResult()
    {
        // 文件标 2024、TMDB 首播 2023 的典型跨年场景：带年零结果，去年份命中
        SetupClient(req => req.Year is null ? 1 : 0);

        TmdbSearchResult result = await _sut.SearchAsync(new TmdbSearchRequest("某剧", "tv", 2024));

        result.Candidates.Should().HaveCount(1);
        _seenRequests.Should().HaveCount(2);
        _seenRequests[0].Year.Should().Be(2024);
        _seenRequests[1].Year.Should().BeNull("第二层应去掉年份重搜");
        _seenRequests[1].Query.Should().Be("某剧");
    }

    [Fact(DisplayName = "回退链重复执行 → 各层各自命中缓存，远端不再被调（键含 year 无污染）")]
    public async Task Search_RepeatAfterFallback_ServedEntirelyFromCache()
    {
        SetupClient(req => req.Year is null ? 1 : 0);

        TmdbSearchResult first = await _sut.SearchAsync(new TmdbSearchRequest("某剧", "tv", 2024));
        first.Candidates.Should().HaveCount(1);
        _seenRequests.Should().HaveCount(2);

        // 第二次执行：带年层命中「零结果缓存」、去年份层命中「有结果缓存」→ 远端调用数不变
        TmdbSearchResult second = await _sut.SearchAsync(new TmdbSearchRequest("某剧", "tv", 2024));
        second.Candidates.Should().HaveCount(1);
        second.FromCache.Should().BeTrue();
        _seenRequests.Should().HaveCount(2, "两层都应命中缓存，不再打远端");
    }

    [Fact(DisplayName = "带年直接命中 → 不触发任何回退层")]
    public async Task Search_PrimaryHit_NoFallbackTriggered()
    {
        SetupClient(_ => 2);

        TmdbSearchResult result = await _sut.SearchAsync(new TmdbSearchRequest("某剧", "tv", 2024));

        result.Candidates.Should().HaveCount(2);
        _seenRequests.Should().HaveCount(1);
        _seenRequests[0].Year.Should().Be(2024);
    }

    // ── 修复 2a：语言注入（请求未显式指定时用 Tmdb_Setting.Language）──

    [Fact(DisplayName = "请求未指定语言 → 注入 Tmdb_Setting.Language/FallbackLanguage")]
    public async Task Search_NoLanguageSpecified_InjectsSettingLanguage()
    {
        UpdateSetting(s => { s.Language = "de-DE"; s.FallbackLanguage = "fr-FR"; });
        SetupClient(_ => 1);

        await _sut.SearchAsync(new TmdbSearchRequest("Film", "movie", 2020));

        _seenRequests.Should().HaveCount(1);
        _seenRequests[0].Language.Should().Be("de-DE", "未显式指定语言时应注入设置值，而非 record 旧默认 zh-CN");
        _seenRequests[0].FallbackLanguage.Should().Be("fr-FR");
    }

    [Fact(DisplayName = "请求显式指定语言 → 优先于设置值")]
    public async Task Search_ExplicitLanguage_OverridesSetting()
    {
        SetupClient(_ => 1);

        await _sut.SearchAsync(new TmdbSearchRequest("作品", "tv", 2020, Language: "ja-JP"));

        _seenRequests.Should().HaveCount(1);
        _seenRequests[0].Language.Should().Be("ja-JP");
    }

    // ── 修复 2b：FallbackLanguage 真实回退（主语言带年→主语言去年→回退语言带年→回退语言去年）──

    [Fact(DisplayName = "主语言两层全零 → 回退语言带年命中（第 3 层）")]
    public async Task Search_PrimaryLanguageZero_FallbackLanguageHits()
    {
        // 仅回退语言 en-US 且带年时命中（模拟中文检索不中、英文可中）
        SetupClient(req => req.Language == "en-US" && req.Year is not null ? 1 : 0);

        TmdbSearchResult result = await _sut.SearchAsync(new TmdbSearchRequest("Some Show", "tv", 2023));

        result.Candidates.Should().HaveCount(1);
        _seenRequests.Should().HaveCount(3);
        (_seenRequests[0].Language, _seenRequests[0].Year).Should().Be(("zh-CN", (int?)2023), "第 1 层：主语言带年");
        (_seenRequests[1].Language, _seenRequests[1].Year).Should().Be(("zh-CN", (int?)null), "第 2 层：主语言去年份");
        (_seenRequests[2].Language, _seenRequests[2].Year).Should().Be(("en-US", (int?)2023), "第 3 层：回退语言带年");
    }

    [Fact(DisplayName = "全四层零结果 → 按序尝试 4 次后返回空结果")]
    public async Task Search_AllLayersZero_ReturnsEmptyAfterFourAttempts()
    {
        SetupClient(_ => 0);

        TmdbSearchResult result = await _sut.SearchAsync(new TmdbSearchRequest("不存在的剧", "tv", 2023));

        result.Candidates.Should().BeEmpty();
        _seenRequests.Should().HaveCount(4);
        (_seenRequests[0].Language, _seenRequests[0].Year).Should().Be(("zh-CN", (int?)2023));
        (_seenRequests[1].Language, _seenRequests[1].Year).Should().Be(("zh-CN", (int?)null));
        (_seenRequests[2].Language, _seenRequests[2].Year).Should().Be(("en-US", (int?)2023));
        (_seenRequests[3].Language, _seenRequests[3].Year).Should().Be(("en-US", (int?)null));
    }

    [Fact(DisplayName = "无年份 + 回退语言等于主语言 → 仅 1 次尝试（链去重）")]
    public async Task Search_NoYearAndSameFallbackLanguage_SingleAttemptOnly()
    {
        UpdateSetting(s => { s.Language = "zh-CN"; s.FallbackLanguage = "zh-CN"; });
        SetupClient(_ => 0);

        TmdbSearchResult result = await _sut.SearchAsync(new TmdbSearchRequest("某剧", "tv"));

        result.Candidates.Should().BeEmpty();
        _seenRequests.Should().HaveCount(1, "无年份去重 + 回退语言与主语言相同去重");
    }

    // ── 修复 3 接线侧：RateLimitPerSecond 随设置流入 ITmdbClient ──

    [Fact(DisplayName = "搜索调用携带 Tmdb_Setting.RateLimitPerSecond")]
    public async Task Search_PassesRateLimitFromSetting()
    {
        UpdateSetting(s => s.RateLimitPerSecond = 33);
        SetupClient(_ => 1);

        await _sut.SearchAsync(new TmdbSearchRequest("某剧", "tv", 2023));

        _seenRates.Should().ContainSingle().Which.Should().Be(33);
    }

    [Fact(DisplayName = "详情调用携带 Tmdb_Setting.RateLimitPerSecond 与语言")]
    public async Task GetDetails_PassesRateLimitAndLanguageFromSetting()
    {
        UpdateSetting(s => { s.RateLimitPerSecond = 18; s.Language = "ko-KR"; });
        _client.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(99, "movie", "标题", "Title", 2020, null, null, null, null, null, null, "{}"));

        await _sut.GetDetailsAsync(99, "movie");

        await _client.Received(1).GetDetailsAsync(99, "movie", Arg.Any<string>(), "ko-KR", 18, Arg.Any<CancellationToken>());
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
