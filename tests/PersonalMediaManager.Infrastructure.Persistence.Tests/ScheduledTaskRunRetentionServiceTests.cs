using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>调度运行记录留存清理 — 固定 30 天天龄闸（A+D 改造配套）</summary>
/// <remarks>
/// 用 in-memory SQLite + EnsureCreated 端到端验证 PurgeAsync：
///   - 删 StartedAt 早于 now-30d 的行；cutoff 处严格 &lt;（恰好等于 cutoff 保留），与 LogRetentionJob 文件清理边界语义一致。
///   - Outcome=Running 僵尸行（进程崩溃残留）同样按 StartedAt 天龄删，无特判。
///   - 空表 / 全部期内 → 返回 0。
/// </remarks>
public sealed class ScheduledTaskRunRetentionServiceTests : IDisposable
{
    /// <summary>固定基准时刻（UTC），所有相对天龄基于此推算</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ScheduledTaskRunRetentionService _sut;

    public ScheduledTaskRunRetentionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new ScheduledTaskRunRetentionService(_dbFactory, new FixedClock(Now));
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "天龄闸：删超期行、留期内行；恰好等于 cutoff 的行保留（严格 <）")]
    public async Task Purge_Deletes_Older_Keeps_AtOrAfter_Cutoff()
    {
        DateTimeOffset cutoff = Now.AddDays(-ScheduledTaskRunRetentionService.RetentionDays);
        long olderId = SeedRun(cutoff.AddSeconds(-1), ScheduledTaskOutcome.Succeeded); // 早于 cutoff 1 秒 → 删
        long atCutoffId = SeedRun(cutoff, ScheduledTaskOutcome.Succeeded);              // 恰好等于 cutoff → 保留
        long newerId = SeedRun(cutoff.AddSeconds(1), ScheduledTaskOutcome.Failed);     // 晚于 cutoff → 保留（Outcome 无关）

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(1, "仅早于 cutoff 的 1 行被删");
        RemainingIds().Should().BeEquivalentTo(new[] { atCutoffId, newerId });
        RemainingIds().Should().NotContain(olderId);
    }

    [Fact(DisplayName = "Running 僵尸行（进程崩溃残留）超期同样被删，无特判")]
    public async Task Purge_Deletes_Expired_Running_Zombie_Rows()
    {
        long zombieId = SeedRun(Now.AddDays(-31), ScheduledTaskOutcome.Running);
        long freshRunningId = SeedRun(Now.AddMinutes(-5), ScheduledTaskOutcome.Running); // 期内 Running（正在跑）→ 保留

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(1, "超期僵尸 Running 行按 StartedAt 天龄删除");
        RemainingIds().Should().BeEquivalentTo(new[] { freshRunningId });
        RemainingIds().Should().NotContain(zombieId);
    }

    [Fact(DisplayName = "空表 / 全部期内 → 返回 0")]
    public async Task Purge_Empty_Or_All_Fresh_Returns_Zero()
    {
        (await _sut.PurgeAsync()).Should().Be(0, "空表无可删");

        SeedRun(Now.AddDays(-1), ScheduledTaskOutcome.Succeeded);
        SeedRun(Now.AddDays(-29), ScheduledTaskOutcome.Failed);

        (await _sut.PurgeAsync()).Should().Be(0, "全部期内不删");
        RemainingIds().Should().HaveCount(2);
    }

    private long SeedRun(DateTimeOffset startedAt, string outcome)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        AuditScheduledTaskRun run = new()
        {
            JobKey = "webhook.webhook-retry",
            StartedAt = startedAt,
            Outcome = outcome,
        };
        ctx.AuditScheduledTaskRuns.Add(run);
        ctx.SaveChanges();
        return run.Id;
    }

    private List<long> RemainingIds()
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.AuditScheduledTaskRuns.AsNoTracking().Select(r => r.Id).ToList();
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

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }
}
