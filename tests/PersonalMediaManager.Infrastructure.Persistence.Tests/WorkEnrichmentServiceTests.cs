using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.Services.Library;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>富化服务 WorkEnrichmentService — 富化写库 / 幂等 / 维度去重 / 关联外键 / 元数据更新 + 远端失败退避</summary>
/// <remarks>
/// in-memory SQLite + EnsureCreated 端到端验证富化落库：EnrichAsync 拉 TMDB 富化详情 → upsert 共享维度
/// （Media_Person/Genre/Company/Network/Keyword，PK=TMDB id 不自增）+ Replace* 原子重建作品连接表（Media_Work*）。
/// ITmdbClient / IProtectedFieldService / IPosterDownloader 用 NSubstitute mock，远端详情由 BuildDetails 构造固定多维度数据。
/// TTL 走 Tmdb_Setting.MetadataCacheHours（种子默认 24h）：首富化后 EnrichedAt=now，二次 force=false 命中 TTL 直接跳过。
/// </remarks>
public sealed class WorkEnrichmentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _scratch;
    private readonly ITmdbClient _client;
    private readonly IPosterDownloader _poster;
    private readonly WorkEnrichmentService _sut;

    public WorkEnrichmentServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
            // 远端路径需要有 ApiKey 才不会在 DecryptApiKey 前置失败
            TmdbSetting s = ctx.TmdbSettings.First(x => x.Id == 1);
            s.ApiKeyEncrypted = "enc";
            ctx.SaveChanges();
        }

        _scratch = Path.Combine(Path.GetTempPath(), $"pmm-enrich-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.ForRoot(_scratch);

        _client = Substitute.For<ITmdbClient>();
        IProtectedFieldService protector = Substitute.For<IProtectedFieldService>();
        protector.Unprotect(Arg.Any<string>()).Returns("realkey");
        _poster = Substitute.For<IPosterDownloader>();

        _sut = new WorkEnrichmentService(
            _dbFactory, protector, _client, _poster, paths,
            new WorkEnrichmentBackoff(), NullLogger<WorkEnrichmentService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ---------- ① 首次富化：插入作品 + 各维度行 + 关联行 ----------

    [Fact(DisplayName = "首次富化：新建 Media_Work + upsert 各维度行 + 重建全部连接表，返回 true")]
    public async Task FirstEnrich_Inserts_Work_Dimensions_And_Joins()
    {
        StubRemote(1396, "tv", BuildDetails(1396, "tv", "绝命毒师", 2008));

        bool enriched = await _sut.EnrichAsync(1396, "tv", force: false);

        enriched.Should().BeTrue("首次富化实际发起了远端拉取");

        using PmmDbContext db = _dbFactory.CreateDbContext();

        // 作品标量已落库
        MediaWork work = await db.MediaWorks.SingleAsync(w => w.TmdbId == 1396 && w.MediaType == "tv");
        work.Title.Should().Be("绝命毒师");
        work.Year.Should().Be(2008);
        work.Overview.Should().Be("一部讲述化学老师的剧集");
        work.VoteAverage.Should().Be(8.9);
        work.EnrichedAt.Should().NotBeNull("富化后应标记 EnrichedAt");

        // 共享维度行（PK=TMDB id）已 upsert：cast(1) + crew(1) 两个人员
        db.MediaPersons.Select(p => p.Id).Should().BeEquivalentTo(new[] { 11, 22 });
        db.MediaGenres.Select(g => g.Id).Should().BeEquivalentTo(new[] { 18, 80 });
        db.MediaCompanies.Select(c => c.Id).Should().BeEquivalentTo(new[] { 100 });
        db.MediaNetworks.Select(n => n.Id).Should().BeEquivalentTo(new[] { 200 });
        db.MediaKeywords.Select(k => k.Id).Should().BeEquivalentTo(new[] { 300, 301 });

        // 连接表行（按 work 过滤）
        db.MediaWorkCredits.Count(c => c.WorkId == work.Id).Should().Be(2, "cast 1 + crew 1");
        db.MediaWorkGenres.Count(g => g.WorkId == work.Id).Should().Be(2);
        db.MediaWorkCompanies.Count(c => c.WorkId == work.Id).Should().Be(1);
        db.MediaWorkNetworks.Count(n => n.WorkId == work.Id).Should().Be(1);
        db.MediaWorkKeywords.Count(k => k.WorkId == work.Id).Should().Be(2);

        // 季摘要随详情 seasons[] 落库
        db.MediaSeasons.Count(s => s.WorkId == work.Id).Should().Be(1);
    }

    // ---------- ② 重复富化幂等（命中 TTL 跳过远端，不产生重复） ----------

    [Fact(DisplayName = "重复富化幂等：二次 force=false 命中 TTL 跳过远端，无重复作品/维度/连接")]
    public async Task SecondEnrich_HitsTtl_NoDuplicates()
    {
        StubRemote(1396, "tv", BuildDetails(1396, "tv", "绝命毒师", 2008));

        bool first = await _sut.EnrichAsync(1396, "tv", force: false);
        bool second = await _sut.EnrichAsync(1396, "tv", force: false);

        first.Should().BeTrue();
        second.Should().BeFalse("EnrichedAt 未过 TTL(24h) 且非 force，直接跳过");

        // 远端仅被打一次
        await _client.Received(1).GetEnrichedDetailsAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaWorks.Count(w => w.TmdbId == 1396).Should().Be(1, "同 TmdbId 不重复建作品");
        db.MediaGenres.Count().Should().Be(2, "维度未被二次插入");
        long workId = db.MediaWorks.Single(w => w.TmdbId == 1396).Id;
        db.MediaWorkGenres.Count(g => g.WorkId == workId).Should().Be(2, "连接表无重复");
        db.MediaWorkCredits.Count(c => c.WorkId == workId).Should().Be(2);
    }

    // ---------- ③ 维度去重：同名/同 id 维度只落一行，复用既有维度 Id ----------

    [Fact(DisplayName = "维度去重：详情内重复 Genre id 只落一行 + 二次富化(force)复用既有维度行不重插")]
    public async Task Genre_Dimension_Deduplicated_And_Reused()
    {
        // 详情内 Genre 18 出现两次（去重源头在 upsert + ReplaceGenres.Distinct）
        TmdbEnrichedDetails first = BuildDetails(1396, "tv", "绝命毒师", 2008) with
        {
            Genres = new List<TmdbGenreRef> { new(18, "剧情"), new(18, "剧情"), new(80, "犯罪") },
        };
        StubRemote(1396, "tv", first);

        await _sut.EnrichAsync(1396, "tv", force: false);

        using (PmmDbContext db1 = _dbFactory.CreateDbContext())
        {
            db1.MediaGenres.Count(g => g.Id == 18).Should().Be(1, "同 id 维度仅一行");
            db1.MediaGenres.Count().Should().Be(2);
            long workId = db1.MediaWorks.Single(w => w.TmdbId == 1396).Id;
            db1.MediaWorkGenres.Count(g => g.WorkId == workId).Should().Be(2, "连接表按 Distinct 去重(18/80)");
        }

        // 二次富化(force 绕过 TTL)：Genre 18 复用既有行，新增 Genre 99；维度表不重插 18
        TmdbEnrichedDetails second = BuildDetails(1396, "tv", "绝命毒师", 2008) with
        {
            Genres = new List<TmdbGenreRef> { new(18, "剧情"), new(99, "悬疑") },
        };
        StubRemote(1396, "tv", second);

        await _sut.EnrichAsync(1396, "tv", force: true);

        using PmmDbContext db2 = _dbFactory.CreateDbContext();
        db2.MediaGenres.Count(g => g.Id == 18).Should().Be(1, "既有维度行被复用，未重插");
        db2.MediaGenres.Select(g => g.Id).Should().BeEquivalentTo(new[] { 18, 80, 99 },
            "新维度 99 入库；80 虽不再被新详情引用，维度行仍保留(连接表只重建)");
        long wid = db2.MediaWorks.Single(w => w.TmdbId == 1396).Id;
        db2.MediaWorkGenres.Where(g => g.WorkId == wid).Select(g => g.GenreId)
            .Should().BeEquivalentTo(new[] { 18, 99 }, "连接表原子替换为新详情维度集");
    }

    // ---------- ④ 关联表外键正确：WorkId 指向 work，维度外键指向维度行 ----------

    [Fact(DisplayName = "关联外键：连接行 WorkId 指向本作品，PersonId/GenreId 指向已 upsert 的维度行")]
    public async Task Join_ForeignKeys_Point_To_Work_And_Dimensions()
    {
        StubRemote(1396, "tv", BuildDetails(1396, "tv", "绝命毒师", 2008));

        await _sut.EnrichAsync(1396, "tv", force: false);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        long workId = db.MediaWorks.Single(w => w.TmdbId == 1396).Id;

        // 全部连接行 WorkId 指向本作品
        db.MediaWorkCredits.Where(c => c.WorkId == workId).Should().NotBeEmpty();
        db.MediaWorkGenres.Should().OnlyContain(g => g.WorkId == workId);

        // Credit.PersonId 全部命中已 upsert 的 Media_Person 行
        List<int> personIds = db.MediaWorkCredits.Where(c => c.WorkId == workId).Select(c => c.PersonId).ToList();
        personIds.Should().BeEquivalentTo(new[] { 11, 22 });
        personIds.Should().OnlyContain(pid => db.MediaPersons.Any(p => p.Id == pid));

        // cast 与 crew 语义分别落库
        MediaWorkCredit cast = db.MediaWorkCredits.Single(c => c.WorkId == workId && c.CreditType == "cast");
        cast.PersonId.Should().Be(11);
        cast.Character.Should().Be("老白");
        cast.Ord.Should().Be(0);
        MediaWorkCredit crew = db.MediaWorkCredits.Single(c => c.WorkId == workId && c.CreditType == "crew");
        crew.PersonId.Should().Be(22);
        crew.Job.Should().Be("Director");
        crew.Department.Should().Be("Directing");

        // GenreId 全部命中已 upsert 的 Media_Genre 行
        db.MediaWorkGenres.Where(g => g.WorkId == workId).Select(g => g.GenreId)
            .Should().OnlyContain(gid => db.MediaGenres.Any(g => g.Id == gid));
    }

    // ---------- ⑤ 更新已有 work 元数据（标题 / 年份 / 评分变化） ----------

    [Fact(DisplayName = "更新元数据：force 二次富化覆盖标题/年份/评分，作品仍单行")]
    public async Task ReEnrich_Force_Updates_Scalar_Metadata_InPlace()
    {
        StubRemote(1396, "tv", BuildDetails(1396, "tv", "旧标题", 2007));
        await _sut.EnrichAsync(1396, "tv", force: false);

        // 远端返回新元数据（标题/年份/评分变化）
        TmdbEnrichedDetails updated = BuildDetails(1396, "tv", "新标题", 2009) with
        {
            VoteAverage = 9.5,
            Overview = "修订后的简介",
        };
        StubRemote(1396, "tv", updated);

        bool reEnriched = await _sut.EnrichAsync(1396, "tv", force: true);

        reEnriched.Should().BeTrue("force 绕过 TTL 仍发起富化");

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaWorks.Count(w => w.TmdbId == 1396 && w.MediaType == "tv").Should().Be(1, "原地更新，不新建作品");
        MediaWork work = db.MediaWorks.Single(w => w.TmdbId == 1396 && w.MediaType == "tv");
        work.Title.Should().Be("新标题");
        work.Year.Should().Be(2009);
        work.VoteAverage.Should().Be(9.5);
        work.Overview.Should().Be("修订后的简介");
    }

    // ---------- 远端失败退避 ----------

    [Fact(DisplayName = "富化退避：远端失败后窗口内二次调用直接跳过远端")]
    public async Task RemoteFailure_BacksOff_SecondCall_SkipsRemote()
    {
        StubRemoteThrows();

        // 第一次：远端抛错 → 透出异常并登记退避
        await _sut.Invoking(s => s.EnrichAsync(1429, "tv", force: false))
            .Should().ThrowAsync<TmdbClientException>();

        // 第二次：命中退避窗口 → 返回 false 且不再打远端
        bool second = await _sut.EnrichAsync(1429, "tv", force: false);
        second.Should().BeFalse();

        await _client.Received(1).GetEnrichedDetailsAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "富化退避：force=true 无视退避窗口仍打远端")]
    public async Task Force_Bypasses_Backoff()
    {
        StubRemoteThrows();

        await _sut.Invoking(s => s.EnrichAsync(1429, "tv", force: false))
            .Should().ThrowAsync<TmdbClientException>();   // 失败 → 退避

        await _sut.Invoking(s => s.EnrichAsync(1429, "tv", force: true))
            .Should().ThrowAsync<TmdbClientException>();    // force 仍打远端 → 再次失败

        await _client.Received(2).GetEnrichedDetailsAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---------- helpers ----------

    /// <summary>桩入富化成功返回固定详情</summary>
    private void StubRemote(int tmdbId, string mediaType, TmdbEnrichedDetails details)
        => _client.GetEnrichedDetailsAsync(tmdbId, mediaType, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(details));

    private void StubRemoteThrows()
        => _client.GetEnrichedDetailsAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TmdbEnrichedDetails>(new TmdbClientException("boom")));

    /// <summary>构造固定的多维度富化详情（2 类型 / 1 公司 / 1 电视台 / 2 关键词 / 1 演员 + 1 导演 / 1 季）</summary>
    private static TmdbEnrichedDetails BuildDetails(int tmdbId, string mediaType, string? title, int? year)
        => new(
            TmdbId: tmdbId,
            MediaType: mediaType,
            Title: title,
            OriginalTitle: "Breaking Bad",
            Year: year,
            Overview: "一部讲述化学老师的剧集",
            Tagline: "All Hail the King",
            PosterPath: "/poster.jpg",
            BackdropPath: "/backdrop.jpg",
            Runtime: 47,
            VoteAverage: 8.9,
            VoteCount: 12000,
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            TmdbStatus: "Ended",
            OriginalLanguage: "en",
            OriginCountry: new List<string> { "US" },
            Homepage: "https://example.test",
            TotalSeasons: 5,
            TotalEpisodes: 62,
            Genres: new List<TmdbGenreRef> { new(18, "剧情"), new(80, "犯罪") },
            Companies: new List<TmdbCompanyRef> { new(100, "Sony Pictures", "/logo.png", "US") },
            Networks: new List<TmdbNetworkRef> { new(200, "AMC", "/amc.png", "US") },
            Keywords: new List<TmdbKeywordRef> { new(300, "drug"), new(301, "chemistry") },
            Cast: new List<TmdbCreditRef> { new(11, "Bryan Cranston", "/p11.jpg", "Acting", "老白", 0, null, null) },
            Crew: new List<TmdbCreditRef> { new(22, "Vince Gilligan", "/p22.jpg", "Directing", null, null, "Director", "Directing") },
            Seasons: new List<TmdbSeasonSummary> { new(1, "第 1 季", "第一季简介", "/s1.jpg", new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero), 7) },
            RawJson: "{}");

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
