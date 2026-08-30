using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Search;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Search;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>统一全局搜索 SearchService — 三域 LIKE 命中 / 库内过滤 / topN / Counts / 边界</summary>
public sealed class SearchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _scratch;
    private readonly AppPaths _paths;
    private readonly SearchService _sut;

    public SearchServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _scratch = Path.Combine(Path.GetTempPath(), $"pmm-search-{Guid.NewGuid():N}");
        _paths = AppPaths.ForRoot(_scratch);
        _sut = new SearchService(_dbFactory, _paths);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ---------- History ----------

    [Fact(DisplayName = "历史：英文文件名大小写折叠命中（SQLite LIKE ASCII 不敏感）")]
    public async Task History_FileName_CaseInsensitive_Hit()
    {
        SeedMediaItem(MediaItemStatus.Completed, fileName: "Inception.2010.1080p.mkv", parsedInfo: """{"title":"盗梦空间","year":2010,"type":"movie"}""");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "inception"));

        r.History.Should().ContainSingle();
        r.History[0].FileName.Should().Be("Inception.2010.1080p.mkv");
        r.History[0].Title.Should().Be("盗梦空间");
        r.History[0].Year.Should().Be(2010);
        r.Counts.History.Should().Be(1);
    }

    [Fact(DisplayName = "历史：中文标题经 ParsedInfo JSON LIKE 命中并平铺 Title/Year")]
    public async Task History_ChineseTitle_In_ParsedInfo_Hit()
    {
        SeedMediaItem(MediaItemStatus.Completed, fileName: "movie_x.mkv", parsedInfo: """{"title":"星际穿越","year":2014,"type":"movie"}""");
        SeedMediaItem(MediaItemStatus.Completed, fileName: "movie_y.mkv", parsedInfo: """{"title":"盗梦空间","year":2010,"type":"movie"}""");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "星际"));

        r.History.Should().ContainSingle();
        r.History[0].Title.Should().Be("星际穿越");
        r.History[0].Year.Should().Be(2014);
    }

    [Fact(DisplayName = "历史：仅终态记录可命中（在途 / 待确认不进历史域）")]
    public async Task History_Only_Terminal_Statuses()
    {
        SeedMediaItem(MediaItemStatus.Completed, fileName: "kw_done.mkv");
        SeedMediaItem(MediaItemStatus.Failed, fileName: "kw_failed.mkv");
        SeedMediaItem(MediaItemStatus.Skipped, fileName: "kw_skipped.mkv");
        SeedMediaItem(MediaItemStatus.Ignored, fileName: "kw_ignored.mkv");
        SeedMediaItem(MediaItemStatus.Parsing, fileName: "kw_parsing.mkv");      // 在途，排除
        SeedMediaItem(MediaItemStatus.AwaitingReview, fileName: "kw_review.mkv"); // 待确认，不进历史

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "kw_", LimitPerGroup: 20));

        r.Counts.History.Should().Be(4);
        r.History.Select(h => h.FileName).Should().BeEquivalentTo(
            new[] { "kw_done.mkv", "kw_failed.mkv", "kw_skipped.mkv", "kw_ignored.mkv" });
    }

    // ---------- Review 隔离 ----------

    [Fact(DisplayName = "待确认：AwaitingReview 进 ReviewHits，不污染历史域")]
    public async Task Review_Isolated_From_History()
    {
        SeedMediaItem(MediaItemStatus.AwaitingReview, fileName: "pending_kw.mkv", parsedInfo: """{"title":"待确认片","year":2021,"type":"movie"}""");
        SeedMediaItem(MediaItemStatus.Completed, fileName: "done_kw.mkv");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "kw"));

        r.Review.Should().ContainSingle();
        r.Review[0].FileName.Should().Be("pending_kw.mkv");
        r.Review[0].Title.Should().Be("待确认片");
        r.Review[0].Year.Should().Be(2021);
        r.Counts.Review.Should().Be(1);

        // 历史只含终态 done_kw，不含待确认项
        r.History.Should().ContainSingle().Which.FileName.Should().Be("done_kw.mkv");
    }

    // ---------- Library ----------

    [Fact(DisplayName = "媒体库：聚合去重 + FileCount = 已归档文件数")]
    public async Task Library_Aggregates_And_FileCount()
    {
        SeedWork(1396, "tv", "绝命毒师", "Breaking Bad", 2008);
        // 同作品键 3 个 Completed 文件 → 一条命中、FileCount=3
        SeedMediaItem(MediaItemStatus.Completed, fileName: "bb_s01e01.mkv", tmdbId: 1396, tmdbMediaType: "tv");
        SeedMediaItem(MediaItemStatus.Completed, fileName: "bb_s01e02.mkv", tmdbId: 1396, tmdbMediaType: "tv");
        SeedMediaItem(MediaItemStatus.Completed, fileName: "bb_s01e03.mkv", tmdbId: 1396, tmdbMediaType: "tv");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "绝命"));

        r.Library.Should().ContainSingle();
        r.Library[0].TmdbId.Should().Be(1396);
        r.Library[0].MediaType.Should().Be("tv");
        r.Library[0].Title.Should().Be("绝命毒师");
        r.Library[0].FileCount.Should().Be(3);
        r.Counts.Library.Should().Be(1);
    }

    [Fact(DisplayName = "媒体库：搜 OriginalTitle（原文名）命中")]
    public async Task Library_Matches_OriginalTitle()
    {
        SeedWork(1396, "tv", "绝命毒师", "Breaking Bad", 2008);
        SeedMediaItem(MediaItemStatus.Completed, fileName: "bb.mkv", tmdbId: 1396, tmdbMediaType: "tv");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "breaking"));

        r.Library.Should().ContainSingle().Which.TmdbId.Should().Be(1396);
    }

    [Fact(DisplayName = "媒体库：Media_Work 存在但无 Completed 文件则不出现（仅搜已归档）")]
    public async Task Library_Excludes_Works_Without_Completed_Files()
    {
        // 作品已富化入库，但文件仅失败 / 待确认 → 不算「库内已归档」
        SeedWork(999, "movie", "未归档片", "Unarchived", 2020);
        SeedMediaItem(MediaItemStatus.Failed, fileName: "unarch.mkv", tmdbId: 999, tmdbMediaType: "movie");
        SeedMediaItem(MediaItemStatus.AwaitingReview, fileName: "unarch2.mkv", tmdbId: 999, tmdbMediaType: "movie");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "未归档"));

        r.Library.Should().BeEmpty();
        r.Counts.Library.Should().Be(0);
    }

    [Fact(DisplayName = "媒体库：HasPoster 反映 posters/{tmdbId}.jpg 是否存在")]
    public async Task Library_HasPoster_Reflects_File()
    {
        SeedWork(100, "movie", "有海报片", null, 2020);
        SeedMediaItem(MediaItemStatus.Completed, fileName: "p1.mkv", tmdbId: 100, tmdbMediaType: "movie");
        File.WriteAllBytes(Path.Combine(_paths.PostersDir, "100.jpg"), new byte[] { 1, 2, 3 });

        SeedWork(200, "movie", "无海报片", null, 2020);
        SeedMediaItem(MediaItemStatus.Completed, fileName: "p2.mkv", tmdbId: 200, tmdbMediaType: "movie");

        GlobalSearchResponse withPoster = await _sut.ListAsync(new GlobalSearchQuery(Q: "有海报"));
        withPoster.Library.Should().ContainSingle().Which.HasPoster.Should().BeTrue();

        GlobalSearchResponse without = await _sut.ListAsync(new GlobalSearchQuery(Q: "无海报"));
        without.Library.Should().ContainSingle().Which.HasPoster.Should().BeFalse();
    }

    // ---------- topN + Counts ----------

    [Fact(DisplayName = "topN：命中超过 LimitPerGroup 时只返 N 条，Counts 仍是真实总数")]
    public async Task TopN_Limits_Items_But_Counts_Total()
    {
        for (int i = 0; i < 8; i++)
            SeedMediaItem(MediaItemStatus.Completed, fileName: $"series_ep{i}.mkv");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "series_ep", LimitPerGroup: 5));

        r.History.Should().HaveCount(5);
        r.Counts.History.Should().Be(8);
    }

    [Fact(DisplayName = "LimitPerGroup 钳到 1..20（传 0 → 1，传 100 → 20）")]
    public async Task LimitPerGroup_Is_Clamped()
    {
        for (int i = 0; i < 3; i++)
            SeedMediaItem(MediaItemStatus.Completed, fileName: $"clamp{i}.mkv");

        GlobalSearchResponse low = await _sut.ListAsync(new GlobalSearchQuery(Q: "clamp", LimitPerGroup: 0));
        low.History.Should().ContainSingle(); // 钳到 1
        low.Counts.History.Should().Be(3);

        GlobalSearchResponse high = await _sut.ListAsync(new GlobalSearchQuery(Q: "clamp", LimitPerGroup: 100));
        high.History.Should().HaveCount(3); // 钳到 20，但只有 3 条
    }

    // ---------- Scopes ----------

    [Fact(DisplayName = "scopes=history 只查历史域，库 / 待确认返回空")]
    public async Task Scopes_History_Only()
    {
        SeedMediaItem(MediaItemStatus.Completed, fileName: "scoped_done.mkv");
        SeedMediaItem(MediaItemStatus.AwaitingReview, fileName: "scoped_review.mkv");
        SeedWork(300, "movie", "scoped 片", null, 2020);
        SeedMediaItem(MediaItemStatus.Completed, fileName: "scoped_lib.mkv", tmdbId: 300, tmdbMediaType: "movie");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "scoped", Scopes: "history"));

        r.History.Should().NotBeEmpty();
        r.Review.Should().BeEmpty();
        r.Library.Should().BeEmpty();
        r.Counts.Review.Should().Be(0);
        r.Counts.Library.Should().Be(0);
    }

    // ---------- 边界 ----------

    [Fact(DisplayName = "空白关键词：返回空结果不抛")]
    public async Task Blank_Keyword_Returns_Empty()
    {
        SeedMediaItem(MediaItemStatus.Completed, fileName: "anything.mkv");

        GlobalSearchResponse r = await _sut.ListAsync(new GlobalSearchQuery(Q: "   "));

        r.Query.Should().BeEmpty();
        r.History.Should().BeEmpty();
        r.Library.Should().BeEmpty();
        r.Review.Should().BeEmpty();
        r.Counts.Should().Be(new SearchCounts(0, 0, 0));
    }

    [Fact(DisplayName = "关键词超 100 字符：截断后查询不抛")]
    public async Task Keyword_Over_100_Chars_Does_Not_Throw()
    {
        string longKw = new string('a', 250);
        Func<Task> act = async () => await _sut.ListAsync(new GlobalSearchQuery(Q: longKw));
        await act.Should().NotThrowAsync();
    }

    // ---------- helpers ----------

    private void SeedMediaItem(MediaItemStatus status, string fileName, string parsedInfo = "{}",
        int? tmdbId = null, string? tmdbMediaType = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item;
        if (status == MediaItemStatus.Failed)
        {
            item = MediaItem.CreateFixture($"/m/{Guid.NewGuid():N}.mkv", fileName, 1024,
                tmdbId: tmdbId, tmdbMediaType: tmdbMediaType, parsedInfo: parsedInfo);
            item.MarkFailed("seed");
        }
        else
        {
            item = MediaItem.CreateFixture($"/m/{Guid.NewGuid():N}.mkv", fileName, 1024,
                status: status, tmdbId: tmdbId, tmdbMediaType: tmdbMediaType, parsedInfo: parsedInfo);
        }
        db.MediaItems.Add(item);
        db.SaveChanges();
    }

    /// <summary>种一部富化作品（Media_Work），设置 Title + OriginalTitle（经 UpsertScalars）</summary>
    private void SeedWork(int tmdbId, string type, string? title, string? originalTitle, int? year)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaWork w = MediaWork.CreateMinimal(tmdbId, type, title, year);
        w.UpsertScalars(title, originalTitle, year, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null);
        db.MediaWorks.Add(w);
        db.SaveChanges();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            opts.AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }
}
