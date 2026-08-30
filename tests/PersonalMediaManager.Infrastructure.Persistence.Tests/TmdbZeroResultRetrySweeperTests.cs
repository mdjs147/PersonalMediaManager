using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;
using Xunit;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>TmdbZeroResultRetrySweeper：「TMDB 未收录」待确认项每日自动重投的筛选与重投行为</summary>
public sealed class TmdbZeroResultRetrySweeperTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IPendingFileQueue _queue;
    private readonly TmdbZeroResultRetrySweeper _sut;
    private readonly string _tempFile;

    public TmdbZeroResultRetrySweeperTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _queue = Substitute.For<IPendingFileQueue>();
        _sut = new TmdbZeroResultRetrySweeper(_dbFactory, _queue, NullLogger<TmdbZeroResultRetrySweeper>.Instance);

        // 真实临时文件：Sweeper 用 File.Exists 校验源文件仍在
        _tempFile = Path.Combine(Path.GetTempPath(), $"pmm-zrr-{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(_tempFile, [0x00]);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    // ---------- 1. 到期记录被重投：AwaitingReview → Queued + 入队 ----------
    [Fact]
    public async Task Due_ZeroResult_Item_Requeued_And_Enqueued()
    {
        long id = SeedZeroResultItem(_tempFile, aiInvolved: false, createdDaysAgo: 2, updatedHoursAgo: 25);

        int n = await _sut.SweepAsync();

        n.Should().Be(1);
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = db.MediaItems.AsNoTracking().Single(x => x.Id == id);
        m.Status.Should().Be(MediaItemStatus.Queued);
        await _queue.Received(1).EnqueueAsync(
            Arg.Is<PendingFileItem>(p => p.FullPath == _tempFile && p.Source == PendingFileSource.Manual),
            Arg.Any<CancellationToken>());
    }

    // ---------- 2. AI 参与过的零结果不重投（重投会再烧 AI）----------
    [Fact]
    public async Task AiInvolved_ZeroResult_Not_Requeued()
    {
        SeedZeroResultItem(_tempFile, aiInvolved: true, createdDaysAgo: 2, updatedHoursAgo: 25);

        int n = await _sut.SweepAsync();

        n.Should().Be(0);
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<PendingFileItem>(), Arg.Any<CancellationToken>());
    }

    // ---------- 3. 距上次动作不足 20 小时不重投（每天最多一次）----------
    [Fact]
    public async Task Recently_Touched_Item_Not_Requeued()
    {
        SeedZeroResultItem(_tempFile, aiInvolved: false, createdDaysAgo: 2, updatedHoursAgo: 3);

        (await _sut.SweepAsync()).Should().Be(0);
    }

    // ---------- 4. 超出窗口天数（默认 14 天）不再重投 ----------
    [Fact]
    public async Task Outside_Retry_Window_Not_Requeued()
    {
        SeedZeroResultItem(_tempFile, aiInvolved: false, createdDaysAgo: 20, updatedHoursAgo: 25);

        (await _sut.SweepAsync()).Should().Be(0);
    }

    // ---------- 5. 开关显式关闭：整体停用 ----------
    [Fact]
    public async Task Disabled_By_Setting_Sweeps_Nothing()
    {
        SeedSetting(TmdbZeroResultRetrySweeper.AutoRetryKey, "false");
        SeedZeroResultItem(_tempFile, aiInvolved: false, createdDaysAgo: 2, updatedHoursAgo: 25);

        (await _sut.SweepAsync()).Should().Be(0);
    }

    // ---------- 6. 窗口天数可配置：设 1 天后，第 2 天的记录不再重投 ----------
    [Fact]
    public async Task Window_Days_Setting_Consumed()
    {
        SeedSetting(TmdbZeroResultRetrySweeper.RetryWindowDaysKey, "1");
        SeedZeroResultItem(_tempFile, aiInvolved: false, createdDaysAgo: 2, updatedHoursAgo: 25);

        (await _sut.SweepAsync()).Should().Be(0);
    }

    // ---------- 7. 源文件已消失：跳过不重投（交由 FileMissing 巡检 / 人工处置）----------
    [Fact]
    public async Task Missing_SourceFile_Skipped()
    {
        string gone = Path.Combine(Path.GetTempPath(), $"pmm-zrr-gone-{Guid.NewGuid():N}.mkv");
        long id = SeedZeroResultItem(gone, aiInvolved: false, createdDaysAgo: 2, updatedHoursAgo: 25);

        int n = await _sut.SweepAsync();

        n.Should().Be(0);
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.AsNoTracking().Single(x => x.Id == id).Status.Should().Be(MediaItemStatus.AwaitingReview);
    }

    // ---------- 8. 其它审核原因（多候选等）不受影响 ----------
    [Fact]
    public async Task Other_ReviewReasons_Not_Touched()
    {
        long id = SeedZeroResultItem(_tempFile, aiInvolved: false, createdDaysAgo: 2, updatedHoursAgo: 25,
            reason: ReviewReason.TmdbMultiCandidate);

        int n = await _sut.SweepAsync();

        n.Should().Be(0);
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.AsNoTracking().Single(x => x.Id == id).Status.Should().Be(MediaItemStatus.AwaitingReview);
    }

    /// <summary>Seed 一条 AwaitingReview 记录：合法状态链推进 + 手写审计时间戳（测试上下文无 TimestampInterceptor）</summary>
    private long SeedZeroResultItem(string sourcePath, bool aiInvolved, int createdDaysAgo, int updatedHoursAgo,
        ReviewReason reason = ReviewReason.TmdbZeroResult)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateDetected(sourcePath, Path.GetFileName(sourcePath), 1);
        m.Transition(MediaItemStatus.Queued);
        m.Transition(MediaItemStatus.Parsing);
        m.Transition(MediaItemStatus.TmdbMatching);
        if (aiInvolved) m.MarkAiInvolved();
        m.MarkAwaitingReview(reason);
        m.CreatedAt = DateTimeOffset.UtcNow.AddDays(-createdDaysAgo);
        m.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-updatedHoursAgo);
        db.MediaItems.Add(m);
        db.SaveChanges();
        return m.Id;
    }

    private void SeedSetting(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Parse", Description = "test" });
        db.SaveChanges();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) => _connection = c;
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }
}
