using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Scan;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Infrastructure.Persistence.Services.Scan;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>整理演练 DryRunService — 决策矩阵预览 + Plex 命名预览，不调 AI / 不动文件 / 不写库</summary>
public sealed class DryRunServiceTests
{
    // ---------- 决策：触发 AI（不真正调用） ----------

    [Fact(DisplayName = "演练：规则置信度不足 → WouldCallAi，且不查 TMDB")]
    public async Task LowConfidence_WouldCallAi_WithoutTmdb()
    {
        DryRunService sut = Sut(Rule("某片", "movie", 2020, conf: 0.3));

        DryRunPreview r = await sut.PreviewAsync("某片.2020.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldCallAi);
        r.TmdbQueried.Should().BeFalse();
        r.Candidates.Should().BeEmpty();
        r.PreviewRelativePath.Should().BeNull();
    }

    [Fact(DisplayName = "演练：命中特殊字符规则 → WouldCallAi")]
    public async Task SpecialChars_WouldCallAi()
    {
        DryRunService sut = Sut(Rule("乱码片", "movie", 2020, conf: 0.95, special: true));

        DryRunPreview r = await sut.PreviewAsync("乱码片.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldCallAi);
        r.TmdbQueried.Should().BeFalse();
        r.HasSpecialChars.Should().BeTrue();
    }

    [Fact(DisplayName = "演练：置信度够但 TMDB 零候选 → WouldCallAi（已查 TMDB）")]
    public async Task HighConfidence_ZeroCandidates_WouldCallAi()
    {
        DryRunService sut = Sut(Rule("冷门片", "movie", 2020, conf: 0.9)); // 无候选

        DryRunPreview r = await sut.PreviewAsync("冷门片.2020.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldCallAi);
        r.TmdbQueried.Should().BeTrue();
        r.Candidates.Should().BeEmpty();
    }

    [Fact(DisplayName = "演练：TMDB 候选超过阈值 → WouldCallAi")]
    public async Task TooManyCandidates_WouldCallAi()
    {
        DryRunService sut = Sut(
            Rule("热词", "movie", 2020, conf: 0.9),
            Cand(1, "movie", "热词 1", 2020), Cand(2, "movie", "热词 2", 2019),
            Cand(3, "movie", "热词 3", 2018), Cand(4, "movie", "热词 4", 2017));

        DryRunPreview r = await sut.PreviewAsync("热词.2020.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldCallAi);
        r.Candidates.Should().HaveCount(4);
    }

    [Fact(DisplayName = "演练：TMDB 调用抛 TmdbClientException → 包成 BusinessException(1000)，与 ReviewService 一致")]
    public async Task TmdbThrows_WrappedAsBusinessException()
    {
        DryRunService sut = new(
            new StubRuleEngine(Rule("某片", "movie", 2020, conf: 0.9)),
            new ThrowingTmdb(),
            Db(),
            NullLogger<DryRunService>.Instance);

        Func<Task> act = () => sut.PreviewAsync("某片.2020.mkv");

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*TMDB*");
    }

    // ---------- 命中：预览命名路径 ----------

    [Fact(DisplayName = "演练：电影命中 → WouldArchive，预览电影相对路径含 tmdb 标记")]
    public async Task Movie_HappyPath_PreviewsPath()
    {
        DryRunService sut = Sut(
            Rule("盗梦空间", "movie", 2010, conf: 0.9),
            Cand(27205, "movie", "盗梦空间", 2010));

        DryRunPreview r = await sut.PreviewAsync("盗梦空间.2010.1080p.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldArchive);
        r.Picked.Should().NotBeNull();
        r.Picked!.TmdbId.Should().Be(27205);
        r.PreviewRelativePath.Should().NotBeNull();
        r.PreviewRelativePath.Should().Contain("盗梦空间 (2010)");
        r.PreviewRelativePath.Should().Contain("{tmdb-27205}");
        r.PreviewRelativePath.Should().EndWith(".mkv");
    }

    [Fact(DisplayName = "演练：标题段用 TMDB 候选规范名，不跟随规则解析名（与实际归档一致）")]
    public async Task PreviewsCanonical_CandidateTitle_Over_RuleTitle()
    {
        DryRunService sut = Sut(
            Rule("Spy x Family", "movie", 2022, conf: 0.9),
            Cand(120089, "movie", "间谍过家家", 2022));

        DryRunPreview r = await sut.PreviewAsync("Spy.x.Family.2022.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldArchive);
        r.PreviewRelativePath.Should().Contain("间谍过家家 (2022)").And.Contain("{tmdb-120089}");
        r.PreviewRelativePath.Should().NotContain("Spy x Family", "标题段应用 TMDB 候选规范名");
    }

    [Fact(DisplayName = "演练：剧集命中 → WouldArchive，预览 Season/SxxExx 路径")]
    public async Task Tv_HappyPath_PreviewsPath()
    {
        DryRunService sut = Sut(
            Rule("绝命毒师", "tv", 2008, conf: 0.9, season: 1, episode: 5),
            Cand(1396, "tv", "绝命毒师", 2008));

        DryRunPreview r = await sut.PreviewAsync("绝命毒师.S01E05.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldArchive);
        r.PreviewRelativePath.Should().Contain("Season 01");
        r.PreviewRelativePath.Should().Contain("S01E05");
    }

    [Fact(DisplayName = "演练：无扩展名 → 用 .mkv 占位预览并在备注说明")]
    public async Task NoExtension_FallsBackToMkv_WithNote()
    {
        DryRunService sut = Sut(
            Rule("盗梦空间", "movie", 2010, conf: 0.9),
            Cand(27205, "movie", "盗梦空间", 2010));

        DryRunPreview r = await sut.PreviewAsync("盗梦空间无后缀");

        r.Outcome.Should().Be(DryRunOutcome.WouldArchive);
        r.PreviewRelativePath.Should().EndWith(".mkv");
        r.PreviewNote.Should().Contain("占位");
    }

    // ---------- 字段不全：转人工 ----------

    [Fact(DisplayName = "演练：剧集缺集号 → WouldReview")]
    public async Task Tv_MissingEpisode_WouldReview()
    {
        DryRunService sut = Sut(
            Rule("绝命毒师", "tv", 2008, conf: 0.9, season: 1, episode: null),
            Cand(1396, "tv", "绝命毒师", 2008));

        DryRunPreview r = await sut.PreviewAsync("绝命毒师.S01.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldReview);
        r.PreviewRelativePath.Should().BeNull();
        r.Picked.Should().NotBeNull();
    }

    [Fact(DisplayName = "演练：年份缺失且候选无年份 → WouldReview")]
    public async Task MissingYear_WouldReview()
    {
        DryRunService sut = Sut(
            Rule("某电影", "movie", year: null, conf: 0.9),
            Cand(99, "movie", "某电影", year: null));

        DryRunPreview r = await sut.PreviewAsync("某电影.mkv");

        r.Outcome.Should().Be(DryRunOutcome.WouldReview);
        r.PreviewRelativePath.Should().BeNull();
        r.PreviewNote.Should().Contain("年份");
    }

    // ---------- 入参校验 ----------
    // 注：空路径校验已迁至 DryRunRequest.Path DataAnnotations（[RequiredNotBlank]），
    // 由边界声明式校验单测覆盖（见 Application.Tests/Validation/DryRunRequestValidationTests）；
    // service 层不再断言空路径（PreviewAsync 入参为裸 string，边界校验在 DryRunRequest 绑定时触发）。

    // ---------- helpers ----------

    private static DryRunService Sut(RuleParseResult rule, params TmdbCandidate[] candidates)
        => new(new StubRuleEngine(rule), new StubTmdb(candidates), Db(), NullLogger<DryRunService>.Instance);

    /// <summary>内存 SQLite 工厂（空库 → 服务读 Tmdb_Setting 缺行回退默认阈值/权重；连接随测试进程回收）</summary>
    private static TestDbContextFactory Db()
    {
        Microsoft.Data.Sqlite.SqliteConnection conn = new("DataSource=:memory:");
        conn.Open();
        TestDbContextFactory factory = new(conn);
        using PmmDbContext ctx = factory.CreateDbContext();
        ctx.Database.EnsureCreated();
        return factory;
    }

    private sealed class TestDbContextFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<PmmDbContext>
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        public TestDbContextFactory(Microsoft.Data.Sqlite.SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PmmDbContext> opts = new();
            Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(opts, _connection);
            return new PmmDbContext(opts.Options);
        }
    }

    private static RuleParseResult Rule(
        string title, string type, int? year, double conf,
        int? season = null, int? episode = null, int? episodeEnd = null,
        bool special = false, long? ruleId = 1)
        => new(title, year, type, season, episode, episodeEnd, conf, special, ruleId);

    private static TmdbCandidate Cand(int id, string type, string? title, int? year, double pop = 1.0)
        => new(id, type, title, null, year, pop, null, null, null, null);

    private sealed class StubRuleEngine : IRuleEngineService
    {
        private readonly RuleParseResult _result;
        public StubRuleEngine(RuleParseResult result) => _result = result;
        public Task<RuleParseResult> ParseAsync(FileParseContext context, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private sealed class StubTmdb : ITmdbSearchService
    {
        private readonly TmdbSearchResult _result;
        public StubTmdb(params TmdbCandidate[] candidates)
            => _result = new TmdbSearchResult(candidates, null);
        public Task<TmdbSearchResult> SearchAsync(TmdbSearchRequest request, CancellationToken ct = default)
            => Task.FromResult(_result);
        public Task<TmdbDetailsResult> GetDetailsAsync(int tmdbId, string mediaType, CancellationToken ct = default)
            => throw new NotImplementedException("演练不应调用 GetDetails");
        public Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(string episodeGroupId, CancellationToken ct = default)
            => throw new NotImplementedException("演练不应调用剧集组");
    }

    /// <summary>模拟 TMDB HTTP 异常的搜索 stub（验证 DryRunService 把 TmdbClientException 映射为 1000）</summary>
    private sealed class ThrowingTmdb : ITmdbSearchService
    {
        public Task<TmdbSearchResult> SearchAsync(TmdbSearchRequest request, CancellationToken ct = default)
            => throw new TmdbClientException("模拟 TMDB HTTP 异常");
        public Task<TmdbDetailsResult> GetDetailsAsync(int tmdbId, string mediaType, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(string episodeGroupId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
