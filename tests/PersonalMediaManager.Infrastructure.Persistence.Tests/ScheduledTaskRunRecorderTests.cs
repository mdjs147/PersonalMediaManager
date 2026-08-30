using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D5.* ScheduledTaskRunRecorder — 起始/终态写入 + 异常路径 + 取消传播</summary>
public sealed class ScheduledTaskRunRecorderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly FakeClock _clock;
    private readonly ScheduledTaskRunRecorder _sut;

    public ScheduledTaskRunRecorderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _clock = new FakeClock(new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero));
        _sut = new ScheduledTaskRunRecorder(_dbFactory, _clock, NullLogger<ScheduledTaskRunRecorder>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task RunAsync_Success_Writes_Succeeded_Row_With_StartedAt_And_DurationMs()
    {
        await _sut.RunAsync("scan.full-scan", "fire-1", _ => Task.CompletedTask, CancellationToken.None);

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.JobKey.Should().Be("scan.full-scan");
        row.FireInstanceId.Should().Be("fire-1");
        row.Outcome.Should().Be(ScheduledTaskOutcome.Succeeded);
        row.StartedAt.Should().Be(_clock.UtcNow);
        row.FinishedAt.Should().Be(_clock.UtcNow);
        row.DurationMs.Should().NotBeNull().And.BeGreaterOrEqualTo(0);
        row.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_WithProcessed_Persists_ProcessedCount()
    {
        await _sut.RunAsync("scan.full-scan", null, ctx =>
        {
            ctx.WithProcessed(42);
            return Task.CompletedTask;
        }, CancellationToken.None);

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.ProcessedCount.Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_WithDetail_Persists_DetailJson()
    {
        await _sut.RunAsync("maintenance.log-retention", null, ctx =>
        {
            ctx.WithDetail("""{"note":"deleted 5 files"}""");
            return Task.CompletedTask;
        }, CancellationToken.None);

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.DetailJson.Should().Be("""{"note":"deleted 5 files"}""");
    }

    [Fact]
    public async Task RunAsync_Business_Exception_Swallowed_And_Marks_Failed_With_ErrorMessage()
    {
        Func<Task> act = async () => await _sut.RunAsync("scan.full-scan", null, _ =>
            throw new InvalidOperationException("某文件夹不可达"), CancellationToken.None);

        await act.Should().NotThrowAsync("Job 业务异常必须吞咽，不污染 Quartz misfire");

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.Outcome.Should().Be(ScheduledTaskOutcome.Failed);
        row.ErrorMessage.Should().Contain("某文件夹不可达");
    }

    [Fact]
    public async Task RunAsync_Cancellation_Marks_Canceled_And_Does_Not_Rethrow()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await _sut.RunAsync("scan.full-scan", null,
            _ => throw new OperationCanceledException(cts.Token), cts.Token);

        await act.Should().NotThrowAsync("取消信号不向 Quartz 上抛，避免污染 misfire 策略；审计已标 Canceled");

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.Outcome.Should().Be(ScheduledTaskOutcome.Canceled);
    }

    [Fact]
    public async Task RunAsync_JobKey_Empty_Throws_ArgumentException()
    {
        Func<Task> act = async () => await _sut.RunAsync(
            jobKey: "", fireInstanceId: null, body: _ => Task.CompletedTask, ct: CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_Truncates_OverlongErrorMessage()
    {
        string longMsg = new('X', 5000);
        await _sut.RunAsync("scan.full-scan", null,
            _ => throw new InvalidOperationException(longMsg), CancellationToken.None);

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.ErrorMessage.Should().NotBeNull();
        row.ErrorMessage!.Length.Should().BeLessOrEqualTo(2000);
    }

    [Fact]
    public async Task RunAsync_SkipIdle_Idle_Success_Writes_No_Row()
    {
        await _sut.RunAsync("webhook.webhook-retry", "fire-idle", ctx =>
        {
            ctx.WithProcessed(0);
            return Task.CompletedTask;
        }, CancellationToken.None, skipAuditWhenIdle: true);

        (await RowCountAsync()).Should().Be(0, "空转（Succeeded + processed=0）必须静默，不留任何行");
    }

    [Fact]
    public async Task RunAsync_SkipIdle_Success_With_Processed_Writes_Single_Completed_Row()
    {
        await _sut.RunAsync("webhook.webhook-retry", "fire-busy", ctx =>
        {
            ctx.WithProcessed(3).WithDetail("""{"note":"picked 3"}""");
            return Task.CompletedTask;
        }, CancellationToken.None, skipAuditWhenIdle: true);

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.JobKey.Should().Be("webhook.webhook-retry");
        row.FireInstanceId.Should().Be("fire-busy");
        row.Outcome.Should().Be(ScheduledTaskOutcome.Succeeded, "延迟写入一次性落终态行，不应存在 Running 中间态");
        row.StartedAt.Should().Be(_clock.UtcNow);
        row.FinishedAt.Should().Be(_clock.UtcNow);
        row.ProcessedCount.Should().Be(3);
        row.DetailJson.Should().Be("""{"note":"picked 3"}""");
        row.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_SkipIdle_Failure_Writes_Failed_Row_Even_With_Zero_Processed()
    {
        Func<Task> act = async () => await _sut.RunAsync("webhook.webhook-retry", null,
            _ => throw new InvalidOperationException("DB 暂时不可达"),
            CancellationToken.None, skipAuditWhenIdle: true);

        await act.Should().NotThrowAsync("延迟写入路径的异常吞咽契约必须与默认路径一致");

        AuditScheduledTaskRun row = await SingleRowAsync();
        row.Outcome.Should().Be(ScheduledTaskOutcome.Failed, "失败必落库——空转静默绝不掩盖故障（r3.15 设计意图）");
        row.ErrorMessage.Should().Contain("DB 暂时不可达");
    }

    [Fact]
    public async Task RunAsync_SkipIdle_Canceled_Idle_Writes_No_Row()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await _sut.RunAsync("webhook.webhook-retry", null,
            _ => throw new OperationCanceledException(cts.Token), cts.Token, skipAuditWhenIdle: true);

        await act.Should().NotThrowAsync("取消信号不向 Quartz 上抛，契约与默认路径一致");

        (await RowCountAsync()).Should().Be(0, "停机取消时未处理任何投递 = 无事发生，不留行");
    }

    private async Task<int> RowCountAsync()
    {
        await using PmmDbContext db = _dbFactory.CreateDbContext();
        return await db.AuditScheduledTaskRuns.AsNoTracking().CountAsync();
    }

    private async Task<AuditScheduledTaskRun> SingleRowAsync()
    {
        await using PmmDbContext db = _dbFactory.CreateDbContext();
        return await db.AuditScheduledTaskRuns.AsNoTracking().SingleAsync();
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
