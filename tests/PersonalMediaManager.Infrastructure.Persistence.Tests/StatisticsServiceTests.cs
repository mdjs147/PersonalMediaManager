using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Statistics;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Statistics;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>统计分析 StatisticsService — 库构成 / 趋势 / 存储聚合（含 EF 翻译落地校验）</summary>
public sealed class StatisticsServiceTests : IDisposable
{
    // 固定 now=2026-06-15，使近 12 月窗口（2025-07..2026-06）确定
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly StatisticsService _sut;

    public StatisticsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new StatisticsService(_dbFactory, new FakeClock(FixedNow));
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "空库：全部返回 0 / 空列表（含 EF 翻译落地，不抛）")]
    public async Task EmptyDb_Returns_Zeros_And_Empty()
    {
        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.Summary.MovieCount.Should().Be(0);
        o.Summary.TvCount.Should().Be(0);
        o.Summary.WorkCount.Should().Be(0);
        o.Summary.FileCount.Should().Be(0);
        o.Summary.TotalSize.Should().Be(0);
        o.Summary.AvgRating.Should().BeNull();
        o.Summary.OldestYear.Should().BeNull();
        o.DecadeDistribution.Should().BeEmpty();
        o.GenreTop.Should().BeEmpty();
        o.CountryTop.Should().BeEmpty();
        o.CategoryDistribution.Should().BeEmpty();
        o.StorageTop.Should().BeEmpty();
        // 趋势：空库 all 档回退近 12 月全零
        o.Range.Should().Be("all");
        o.TrendGranularity.Should().Be("month");
        o.Trend.Should().HaveCount(12);
        o.Trend.Should().OnlyContain(p => p.Count == 0);
        o.RatingHistogram.Should().HaveCount(5);
        // 识别构成：空库全 0（含 ParseSource GroupBy 与 EXISTS 子查询的 SQLite 翻译落地）
        o.Identification.Total.Should().Be(0);
        o.Identification.AiInvolvedCount.Should().Be(0);
        o.Identification.ReviewInvolvedCount.Should().Be(0);
    }

    [Fact(DisplayName = "汇总：电影/剧集数、文件数、总容量、年份跨度按库内已完成作品口径")]
    public async Task Summary_Counts_And_Span()
    {
        SeedCompletedWork(1, "movie", "盗梦空间", 2010, vote: 8.4, fileSize: 1000);
        SeedCompletedWork(2, "movie", "黑客帝国", 1999, vote: 8.2, fileSize: 2000);
        SeedCompletedWork(3, "tv", "繁花", 2023, vote: 7.5, fileSize: 500);
        // 有 MediaWork 但无 Completed 文件 → 不计入库内口径
        SeedWorkOnly(9, "movie", 1980);

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.Summary.MovieCount.Should().Be(2);
        o.Summary.TvCount.Should().Be(1);
        o.Summary.WorkCount.Should().Be(3);
        o.Summary.FileCount.Should().Be(3);
        o.Summary.TotalSize.Should().Be(3500);
        o.Summary.RatedCount.Should().Be(3);
        // 服务对均分 Math.Round(…,1)：(8.4+8.2+7.5)/3 = 8.033 → 8.0
        o.Summary.AvgRating.Should().BeApproximately(8.0, 0.001);
        o.Summary.OldestYear.Should().Be(1999, "未入库的 1980 不计入");
        o.Summary.NewestYear.Should().Be(2023);
    }

    [Fact(DisplayName = "年代分布：按十年代分桶并升序")]
    public async Task Decade_Distribution_Buckets()
    {
        SeedCompletedWork(1, "movie", "A", 1994);
        SeedCompletedWork(2, "movie", "B", 1999);
        SeedCompletedWork(3, "movie", "C", 2010);
        SeedCompletedWork(4, "tv", "D", 2023);

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.DecadeDistribution.Select(d => (d.Decade, d.Count))
            .Should().ContainInOrder((1990, 2), (2010, 1), (2020, 1));
    }

    [Fact(DisplayName = "评分直方图：固定 5 桶顺序 + 正确落桶")]
    public async Task Rating_Histogram_Buckets()
    {
        SeedCompletedWork(1, "movie", "低", 2020, vote: 5.5);   // <6
        SeedCompletedWork(2, "movie", "中", 2020, vote: 7.0);   // 7~8
        SeedCompletedWork(3, "movie", "高", 2020, vote: 8.5);   // 8~9
        SeedCompletedWork(4, "movie", "神", 2020, vote: 9.6);   // 9~10

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.RatingHistogram.Select(b => b.Label).Should().ContainInOrder("<6", "6~7", "7~8", "8~9", "9~10");
        o.RatingHistogram.Single(b => b.Label == "<6").Count.Should().Be(1);
        o.RatingHistogram.Single(b => b.Label == "7~8").Count.Should().Be(1);
        o.RatingHistogram.Single(b => b.Label == "8~9").Count.Should().Be(1);
        o.RatingHistogram.Single(b => b.Label == "9~10").Count.Should().Be(1);
        o.RatingHistogram.Single(b => b.Label == "6~7").Count.Should().Be(0);
    }

    [Fact(DisplayName = "出品国家 Top：按作品数降序")]
    public async Task Country_Top()
    {
        SeedCompletedWork(1, "movie", "美一", 2020, country: "US");
        SeedCompletedWork(2, "movie", "美二", 2021, country: "US");
        SeedCompletedWork(3, "tv", "日一", 2022, country: "JP");

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.CountryTop.Should().HaveCount(2);
        o.CountryTop[0].Name.Should().Be("US");
        o.CountryTop[0].Count.Should().Be(2);
        o.CountryTop[1].Name.Should().Be("JP");
    }

    [Fact(DisplayName = "分类构成：按 Media_Work.CategoryId 聚合并取分类名")]
    public async Task Category_Distribution()
    {
        long movieCat = SeedCategory("电影");
        long tvCat = SeedCategory("电视剧");
        SeedCompletedWork(1, "movie", "片一", 2020, categoryId: movieCat);
        SeedCompletedWork(2, "movie", "片二", 2021, categoryId: movieCat);
        SeedCompletedWork(3, "tv", "剧一", 2022, categoryId: tvCat);

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.CategoryDistribution.Should().HaveCount(2);
        o.CategoryDistribution[0].Name.Should().Be("电影");
        o.CategoryDistribution[0].Count.Should().Be(2);
        o.CategoryDistribution.Single(c => c.Name == "电视剧").Count.Should().Be(1);
    }

    [Fact(DisplayName = "类型 Top：经连接表聚合（共享类型按作品数累计）")]
    public async Task Genre_Top_From_Join()
    {
        SeedGenreDim(18, "剧情");
        SeedGenreDim(28, "动作");
        SeedCompletedWorkWithGenres(1, "movie", "片A", new[] { 18, 28 });
        SeedCompletedWorkWithGenres(2, "movie", "片B", new[] { 18 });

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.GenreTop[0].Name.Should().Be("剧情");
        o.GenreTop[0].Count.Should().Be(2);
        o.GenreTop.Single(g => g.Name == "动作").Count.Should().Be(1);
    }

    [Fact(DisplayName = "存储 Top：按作品聚合 Completed 文件大小并降序")]
    public async Task Storage_Top_Ordered_By_Size()
    {
        // 剧集多文件累加：tmdb 100 两集各 700 = 1400
        SeedCompletedWork(100, "tv", "大剧", 2020, fileSize: 700);
        SeedCompletedItemOnly(100, "tv", fileSize: 700);
        SeedCompletedWork(200, "movie", "小片", 2021, fileSize: 300);

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.StorageTop.Should().HaveCount(2);
        o.StorageTop[0].TmdbId.Should().Be(100);
        o.StorageTop[0].Size.Should().Be(1400);
        o.StorageTop[0].Title.Should().Be("大剧");
        o.StorageTop[1].TmdbId.Should().Be(200);
        o.StorageTop[1].Size.Should().Be(300);
    }

    [Fact(DisplayName = "入库趋势(all)：按月从最早归档月铺到当前月")]
    public async Task Trend_All_From_Earliest_Month()
    {
        SeedArchived(1, new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
        SeedArchived(2, new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));
        SeedArchived(3, new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero));
        SeedArchived(4, new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero));

        StatisticsOverview o = await _sut.GetOverviewAsync("all");

        o.TrendGranularity.Should().Be("month");
        o.Trend.Should().HaveCount(14, "2025-05 → 2026-06 含头尾共 14 个月");
        o.Trend[0].Bucket.Should().Be("2025-05");
        o.Trend[^1].Bucket.Should().Be("2026-06");
        o.Trend.Single(p => p.Bucket == "2026-06").Count.Should().Be(2);
        o.Trend.Single(p => p.Bucket == "2026-05").Count.Should().Be(1);
        o.Trend.Single(p => p.Bucket == "2025-05").Count.Should().Be(1);
    }

    [Fact(DisplayName = "入库趋势(1y)：近 12 月窗口，更早归档不计")]
    public async Task Trend_1y_Window_Excludes_Older()
    {
        SeedArchived(1, new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
        SeedArchived(2, new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero));
        SeedArchived(3, new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero)); // 13 月前，窗口外

        StatisticsOverview o = await _sut.GetOverviewAsync("1y");

        o.Trend.Should().HaveCount(12);
        o.Trend[0].Bucket.Should().Be("2025-07");
        o.Trend[^1].Bucket.Should().Be("2026-06");
        o.Trend.Sum(p => p.Count).Should().Be(2, "2025-05 在近 12 月窗口外被排除");
    }

    [Fact(DisplayName = "识别构成：ParseSource 五类分组 + AiInvolved 计数（仅 Completed 计入）")]
    public async Task Identification_Counts_By_Source_And_AiInvolved()
    {
        SeedCompletedItem(ParseSource.Rule);
        SeedCompletedItem(ParseSource.Rule);
        SeedCompletedItem(ParseSource.Ai, aiInvolved: true);
        SeedCompletedItem(ParseSource.Hybrid, aiInvolved: true);
        SeedCompletedItem(ParseSource.Manual);
        SeedCompletedItem(null); // ParseSource 为 null → 未识别
        SeedReviewPendingItem(); // 非 Completed（AwaitingReview 且 AiInvolved），基线外不计

        StatisticsOverview o = await _sut.GetOverviewAsync();

        o.Identification.Total.Should().Be(6);
        o.Identification.RuleCount.Should().Be(2);
        o.Identification.AiCount.Should().Be(1, "非 Completed 的 Ai 记录不计入");
        o.Identification.HybridCount.Should().Be(1);
        o.Identification.ManualCount.Should().Be(1);
        o.Identification.OtherCount.Should().Be(1);
        o.Identification.AiInvolvedCount.Should().Be(2, "非 Completed 的 AiInvolved 不计入");
        o.Identification.ReviewInvolvedCount.Should().Be(0);
    }

    [Fact(DisplayName = "识别构成：人工审核介入按时间线 EXISTS 计数 + 归档时间窗过滤")]
    public async Task Identification_ReviewInvolved_And_Window_Filter()
    {
        // FixedNow=2026-06-15，30d 窗 ≈ 2026-05-16..2026-06-15
        long inWindowReviewed = SeedCompletedItem(ParseSource.Ai, aiInvolved: true,
            archivedAt: new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        long inWindowClean = SeedCompletedItem(ParseSource.Rule,
            archivedAt: new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero));
        long outWindowReviewed = SeedCompletedItem(ParseSource.Ai, aiInvolved: true,
            archivedAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)); // 窗口外
        SeedStep(inWindowReviewed, MediaItemStatus.AwaitingReview);
        SeedStep(inWindowClean, MediaItemStatus.Parsing); // 非审核步骤不算介入
        SeedStep(outWindowReviewed, MediaItemStatus.AwaitingReview);

        StatisticsOverview all = await _sut.GetOverviewAsync("all");
        all.Identification.Total.Should().Be(3);
        all.Identification.ReviewInvolvedCount.Should().Be(2);
        all.Identification.AiInvolvedCount.Should().Be(2);

        StatisticsOverview o = await _sut.GetOverviewAsync("30d");
        o.Identification.Total.Should().Be(2);
        o.Identification.ReviewInvolvedCount.Should().Be(1, "窗口外的已审核记录整条排除");
        o.Identification.AiInvolvedCount.Should().Be(1);
        o.Identification.AiCount.Should().Be(1);
        o.Identification.RuleCount.Should().Be(1);
    }

    [Fact(DisplayName = "入库趋势(30d)：按天 30 桶 + 归档时间窗过滤库构成")]
    public async Task Trend_30d_Daily_And_Filters_Composition()
    {
        // FixedNow=2026-06-15，30d 窗 ≈ 2026-05-16..2026-06-15
        SeedCompletedWork(1, "movie", "近", 2024, fileSize: 100, archivedAt: new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        SeedCompletedWork(2, "movie", "远", 2024, fileSize: 200, archivedAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)); // 窗口外

        StatisticsOverview o = await _sut.GetOverviewAsync("30d");

        o.TrendGranularity.Should().Be("day");
        o.Trend.Should().HaveCount(30);
        o.Trend[^1].Bucket.Should().Be("2026-06-15");
        o.Trend.Single(p => p.Bucket == "2026-06-10").Count.Should().Be(1);
        // 库构成按归档窗过滤：仅「近」计入
        o.Summary.MovieCount.Should().Be(1);
        o.Summary.FileCount.Should().Be(1);
        o.Summary.TotalSize.Should().Be(100);
    }

    // ---------- helpers ----------

    /// <summary>种一部「库内已完成作品」：Media_Work（可选评分/国家/分类）+ 一条匹配的 Completed Media_Item</summary>
    private void SeedCompletedWork(int tmdbId, string type, string title, int year,
        double? vote = null, string? country = null, long? categoryId = null, long fileSize = 1,
        DateTimeOffset? archivedAt = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaWork w = MediaWork.CreateMinimal(tmdbId, type, title, year);
        w.UpsertScalars(title, null, year, null, null, null, null, null, vote, null, null, null, "en",
            country is null ? null : new List<string> { country }, null, null, null);
        if (categoryId is not null) w.SetCategory(categoryId);
        db.MediaWorks.Add(w);
        MediaItem item = NewCompleted(tmdbId, type, title, year, fileSize, categoryId);
        db.MediaItems.Add(item);
        db.SaveChanges();
        // 趋势/时间窗测试需要真实 ArchivedAt（CreateFixture 不设，ExecuteUpdate 绕拦截器写入）
        if (archivedAt is not null)
            db.MediaItems.Where(x => x.Id == item.Id).ExecuteUpdate(s => s.SetProperty(p => p.ArchivedAt, archivedAt));
    }

    /// <summary>仅追加一条 Completed 文件（用于同作品多文件存储累加）</summary>
    private void SeedCompletedItemOnly(int tmdbId, string type, long fileSize)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Add(NewCompleted(tmdbId, type, $"work{tmdbId}", 2020, fileSize, null));
        db.SaveChanges();
    }

    /// <summary>仅种 Media_Work（无 Completed 文件 → 不属库内口径）</summary>
    private void SeedWorkOnly(int tmdbId, string type, int year)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaWorks.Add(MediaWork.CreateMinimal(tmdbId, type, $"work{tmdbId}", year));
        db.SaveChanges();
    }

    /// <summary>种一部带类型连接的库内已完成作品</summary>
    private void SeedCompletedWorkWithGenres(int tmdbId, string type, string title, int[] genreIds)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaWork w = MediaWork.CreateMinimal(tmdbId, type, title, 2020);
        db.MediaWorks.Add(w);
        db.MediaItems.Add(NewCompleted(tmdbId, type, title, 2020, 1, null));
        db.SaveChanges();
        w.ReplaceGenres(genreIds);
        db.SaveChanges();
    }

    /// <summary>种一条带 ArchivedAt 的 Completed 文件（趋势用；ExecuteUpdate 绕拦截器写过去时间）</summary>
    private void SeedArchived(int tmdbId, DateTimeOffset archivedAt)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = NewCompleted(tmdbId, "movie", $"a{tmdbId}", 2020, 1, null);
        db.MediaItems.Add(m);
        db.SaveChanges();
        db.MediaItems.Where(x => x.Id == m.Id).ExecuteUpdate(s => s.SetProperty(p => p.ArchivedAt, archivedAt));
    }

    /// <summary>种一条指定 ParseSource / AiInvolved 的 Completed 文件（识别构成用），返回 Id</summary>
    private long SeedCompletedItem(ParseSource? source, bool aiInvolved = false, DateTimeOffset? archivedAt = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateFixture(
            sourcePath: $"/m/{Guid.NewGuid():N}.mkv",
            fileName: "f.mkv",
            fileSize: 1,
            status: MediaItemStatus.Completed,
            parseSource: source);
        if (aiInvolved) m.MarkAiInvolved();
        db.MediaItems.Add(m);
        db.SaveChanges();
        // 时间窗测试需要真实 ArchivedAt（CreateFixture 不设，ExecuteUpdate 绕拦截器写入）
        if (archivedAt is not null)
            db.MediaItems.Where(x => x.Id == m.Id).ExecuteUpdate(s => s.SetProperty(p => p.ArchivedAt, archivedAt));
        return m.Id;
    }

    /// <summary>种一条滞留人工确认（非 Completed）且 AiInvolved 的记录：验证基线过滤</summary>
    private void SeedReviewPendingItem()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateFixture(
            sourcePath: $"/m/{Guid.NewGuid():N}.mkv",
            fileName: "pending.mkv",
            fileSize: 1,
            status: MediaItemStatus.AwaitingReview,
            parseSource: ParseSource.Ai,
            reviewReason: ReviewReason.AiLowConfidence);
        m.MarkAiInvolved();
        db.MediaItems.Add(m);
        db.SaveChanges();
    }

    /// <summary>为指定媒体挂一条时间线步骤（人工审核介入 EXISTS 口径用）</summary>
    private void SeedStep(long mediaItemId, MediaItemStatus stage)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ProcessSteps.Add(ProcessStep.CreateFixture(mediaItemId, stage, FixedNow));
        db.SaveChanges();
    }

    private void SeedGenreDim(int id, string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        if (!db.MediaGenres.Any(g => g.Id == id))
        {
            db.MediaGenres.Add(new MediaGenre { Id = id, Name = name });
            db.SaveChanges();
        }
    }

    private long SeedCategory(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new() { Name = name, MediaType = MediaType.Both, TargetRoot = "/M" };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    private static MediaItem NewCompleted(int tmdbId, string type, string title, int year, long fileSize, long? categoryId)
        => MediaItem.CreateFixture(
            sourcePath: $"/m/{Guid.NewGuid():N}.mkv",
            fileName: $"{title}.mkv",
            fileSize: fileSize,
            status: MediaItemStatus.Completed,
            tmdbId: tmdbId,
            tmdbMediaType: type,
            parseSource: ParseSource.Rule,
            parsedInfo: new ParsedInfo(title, year, type, null, null, null, null).ToJson(),
            categoryId: categoryId);

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

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }
}
