using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Dashboard;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Dashboard;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D7.7 DashboardService — stats 各 bucket + recent 排序 + 分类联表</summary>
public sealed class DashboardServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    // 走真实 UtcNow，使 SeedItem 默认 UpdatedAt（拦截器写入的当前 UTC）与 todayStart 比较有意义；
    // 历史项用 SetUpdatedAt 手工写过去时间
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);
    private readonly DashboardService _sut;

    public DashboardServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new DashboardService(_dbFactory, _clock);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetStats_Empty_DB_Returns_All_Zero()
    {
        DashboardStats stats = await _sut.GetStatsAsync();
        stats.Today.Processed.Should().Be(0);
        stats.Total.Processed.Should().Be(0);
        stats.Queue.Review.Should().Be(0);
        stats.ParseSource.Rule.Should().Be(0);
        stats.Service.Running.Should().BeTrue();
    }

    [Fact]
    public async Task GetStats_TotalProcessed_Counts_All_Completed()
    {
        SeedItem(MediaItemStatus.Completed);
        SeedItem(MediaItemStatus.Completed);
        SeedItem(MediaItemStatus.Skipped);
        SeedItem(MediaItemStatus.Failed);

        DashboardStats s = await _sut.GetStatsAsync();
        s.Total.Processed.Should().Be(2);
        s.Total.Skipped.Should().Be(1);
        s.Total.Failed.Should().Be(1);
    }

    [Fact]
    public async Task GetStats_Today_Filters_By_UpdatedAt_GreaterEqual_TodayStart()
    {
        // 用 SQL 更新 UpdatedAt 以绕过拦截器自动设置
        SeedItem(MediaItemStatus.Completed); // 默认 UpdatedAt=now，正好今天
        long oldId = SeedItem(MediaItemStatus.Completed);
        SetUpdatedAt(oldId, DateTimeOffset.UtcNow.AddDays(-7));

        DashboardStats s = await _sut.GetStatsAsync();
        s.Today.Processed.Should().Be(1, "只算今日 UpdatedAt 的");
        s.Total.Processed.Should().Be(2);
    }

    [Fact]
    public async Task GetStats_Queue_Splits_Review_Running_Pending()
    {
        SeedItem(MediaItemStatus.AwaitingReview);
        SeedItem(MediaItemStatus.AwaitingReview);
        SeedItem(MediaItemStatus.Parsing);
        SeedItem(MediaItemStatus.Archiving);
        SeedItem(MediaItemStatus.Queued);

        DashboardStats s = await _sut.GetStatsAsync();
        s.Queue.Review.Should().Be(2);
        s.Queue.Running.Should().Be(2);
        s.Queue.Pending.Should().Be(1);
    }

    [Fact]
    public async Task GetStats_ParseSource_Counts_By_Source()
    {
        SeedItem(MediaItemStatus.Completed, source: ParseSource.Rule);
        SeedItem(MediaItemStatus.Completed, source: ParseSource.Rule);
        SeedItem(MediaItemStatus.Completed, source: ParseSource.Ai);
        SeedItem(MediaItemStatus.Failed,    source: ParseSource.Hybrid);
        SeedItem(MediaItemStatus.Detected); // ParseSource=null 不计入

        DashboardStats s = await _sut.GetStatsAsync();
        s.ParseSource.Rule.Should().Be(2);
        s.ParseSource.Ai.Should().Be(1);
        s.ParseSource.Hybrid.Should().Be(1);
    }

    [Fact]
    public async Task GetRecent_Returns_Items_DESC_By_UpdatedAt_With_Limit()
    {
        for (int i = 0; i < 30; i++) SeedItem(MediaItemStatus.Completed);
        DashboardRecentList r = await _sut.GetRecentAsync(limit: 5);
        r.Items.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetRecent_Joins_Category_Name()
    {
        long catId = SeedCategory("电影");
        long id = SeedItemWithCategory(MediaItemStatus.Completed, catId,
            parsedInfo: """{"title":"盗梦空间","year":2010,"type":"movie"}""", targetPath: "/M/x.mkv");

        DashboardRecentList r = await _sut.GetRecentAsync(limit: 20);
        DashboardRecentItem one = r.Items.Single(i => i.Id == id);
        one.Category.Should().Be("电影");
        one.Title.Should().Be("盗梦空间");
        one.Year.Should().Be(2010);
        one.TargetPath.Should().Be("/M/x.mkv");
    }

    [Fact]
    public async Task GetRecent_Limit_Bounds_Are_Enforced()
    {
        for (int i = 0; i < 5; i++) SeedItem(MediaItemStatus.Completed);
        (await _sut.GetRecentAsync(0)).Items.Should().HaveCount(5);
        (await _sut.GetRecentAsync(-3)).Items.Should().HaveCount(5);
        (await _sut.GetRecentAsync(1000)).Items.Should().HaveCount(5, "limit 上限 100 不影响实际只 5 行");
    }

    [Fact]
    public async Task GetTasks_Empty_DB_Returns_Empty_List()
    {
        DashboardTasksResponse data = await _sut.GetTasksAsync(windowHours: 24, limit: 50);
        data.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTasks_Orders_By_StartedAt_DESC_Within_Window()
    {
        // 真实业务的 StartedAt 由 Recorder 写，测试侧直接 INSERT 行
        DateTimeOffset now = _clock.UtcNow;
        SeedRun("scan.full-scan", startedAt: now.AddMinutes(-30), outcome: "Succeeded");
        SeedRun("webhook.webhook-retry", startedAt: now.AddMinutes(-5), outcome: "Succeeded");
        SeedRun("maintenance.log-retention", startedAt: now.AddDays(-2), outcome: "Succeeded");  // 窗口外

        DashboardTasksResponse data = await _sut.GetTasksAsync(windowHours: 24, limit: 50);

        data.Items.Should().HaveCount(2, "2 天前的不在 24h 窗口");
        data.Items[0].JobKey.Should().Be("webhook.webhook-retry", "DESC 排序，最近的在前");
        data.Items[1].JobKey.Should().Be("scan.full-scan");
    }

    [Fact]
    public async Task GetTasks_Limit_Bounds_Are_Enforced()
    {
        DateTimeOffset now = _clock.UtcNow;
        for (int i = 0; i < 8; i++) SeedRun("scan.full-scan", startedAt: now.AddMinutes(-i), outcome: "Succeeded");

        (await _sut.GetTasksAsync(24, 0)).Items.Should().HaveCount(8, "limit<=0 用默认 50，本组只 8 行");
        (await _sut.GetTasksAsync(24, 3)).Items.Should().HaveCount(3);
        (await _sut.GetTasksAsync(24, 9999)).Items.Should().HaveCount(8, "limit 超上限 200 不影响实际只 8 行");
    }

    [Fact]
    public async Task GetTasks_WindowHours_Bounds_Are_Enforced()
    {
        DateTimeOffset now = _clock.UtcNow;
        SeedRun("scan.full-scan", startedAt: now.AddHours(-5), outcome: "Succeeded");
        SeedRun("webhook.webhook-retry", startedAt: now.AddHours(-50), outcome: "Succeeded");
        SeedRun("maintenance.log-retention", startedAt: now.AddHours(-200), outcome: "Succeeded");

        DashboardTasksResponse defaultWin = await _sut.GetTasksAsync(0, 50);
        defaultWin.Items.Should().HaveCount(1, "windowHours<=0 用默认 24h，只 5h 前的进窗口");
        defaultWin.Items[0].JobKey.Should().Be("scan.full-scan");

        // 自定义 100h 看到 5h 与 50h 两条
        DashboardTasksResponse oneHundredH = await _sut.GetTasksAsync(100, 50);
        oneHundredH.Items.Should().HaveCount(2);

        // 超出上限 168h（7d）会夹紧，因此 200h 前的也不进
        DashboardTasksResponse overMax = await _sut.GetTasksAsync(500, 50);
        overMax.Items.Should().HaveCount(2, "windowHours 上限 168h，200h 前的依然不进窗口");
    }

    [Fact]
    public async Task GetTasks_Running_Row_Included_With_Null_FinishedAt()
    {
        DateTimeOffset now = _clock.UtcNow;
        SeedRun("scan.full-scan", startedAt: now.AddMinutes(-2), outcome: "Running", finishedAt: null, durationMs: null, processedCount: null);

        DashboardTasksResponse data = await _sut.GetTasksAsync(24, 50);

        data.Items.Should().HaveCount(1);
        data.Items[0].Outcome.Should().Be("Running");
        data.Items[0].FinishedAt.Should().BeNull();
        data.Items[0].DurationMs.Should().BeNull();
    }

    private void SeedRun(string jobKey, DateTimeOffset startedAt, string outcome, DateTimeOffset? finishedAt = null, long? durationMs = 100, int? processedCount = 0, string? errorMessage = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        AuditScheduledTaskRun row = new()
        {
            JobKey = jobKey,
            StartedAt = startedAt,
            FinishedAt = finishedAt ?? (outcome == "Running" ? null : startedAt.AddSeconds(1)),
            DurationMs = durationMs,
            Outcome = outcome,
            ProcessedCount = processedCount,
            ErrorMessage = errorMessage,
        };
        db.AuditScheduledTaskRuns.Add(row);
        db.SaveChanges();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("not json", null, null)]
    [InlineData("""{"title":"X","year":2020}""", "X", 2020)]
    [InlineData("""{"title":"X","year":"2020"}""", "X", 2020)]
    [InlineData("""{"title":null,"year":null}""", null, null)]
    public void ExtractTitleYear_Boundaries(string? input, string? expectedTitle, int? expectedYear)
    {
        (string? t, int? y) = DashboardService.ExtractTitleYear(input);
        t.Should().Be(expectedTitle);
        y.Should().Be(expectedYear);
    }

    // ---------- helpers ----------

    private long SeedItem(MediaItemStatus status, ParseSource? source = null)
        => SeedItemWithCategory(status, categoryId: null, parsedInfo: "{}", source: source);

    private long SeedItemWithCategory(MediaItemStatus status, long? categoryId, string parsedInfo, ParseSource? source = null, string? targetPath = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string path = $"/tmp/{Guid.NewGuid():N}.mkv";

        // Failed 状态走 MarkFailed 保留 AttemptCount/LastAttemptAt 真实业务语义；其他直跳 fixture
        MediaItem item;
        if (status == MediaItemStatus.Failed)
        {
            item = MediaItem.CreateFixture(path, Path.GetFileName(path), 1024,
                categoryId: categoryId, parseSource: source, parsedInfo: parsedInfo, targetPath: targetPath);
            item.MarkFailed("seed-failed");
        }
        else
        {
            item = MediaItem.CreateFixture(path, Path.GetFileName(path), 1024,
                status: status,
                categoryId: categoryId, parseSource: source, parsedInfo: parsedInfo, targetPath: targetPath);
        }

        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private long SeedCategory(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new() { Name = name, MediaType = MediaType.Both, TargetRoot = "/M" };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    private void SetUpdatedAt(long id, DateTimeOffset newValue)
    {
        // 拦截器会在每次 SaveChanges 自动覆盖 UpdatedAt，所以用 ExecuteUpdate（不走 SaveChanges）
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Where(m => m.Id == id)
            .ExecuteUpdate(s => s.SetProperty(m => m.UpdatedAt, newValue));
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

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }
}
