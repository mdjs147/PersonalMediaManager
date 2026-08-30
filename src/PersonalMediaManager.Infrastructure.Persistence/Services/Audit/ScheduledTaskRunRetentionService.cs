using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

/// <summary>Audit_ScheduledTaskRun 留存清理实现（固定 30 天天龄闸）</summary>
/// <remarks>
/// 独立短期 ctx + ExecuteDeleteAsync 直删不经变更跟踪（与 AiCallRetentionService 同构）。
/// 删除条件 StartedAt &lt; now - 30d：边界行（= cutoff）保留，与 LogRetentionJob 文件清理语义一致；
/// Running 僵尸行（进程崩溃残留）天龄一到同样被删，兑现 AuditScheduledTaskRun 实体注释预留的清理策略。
/// </remarks>
internal sealed class ScheduledTaskRunRetentionService : IScheduledTaskRunRetentionService
{
    /// <summary>保留天数（与日志文件 30 天一致，常量不走 System_Setting）</summary>
    public const int RetentionDays = 30;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public ScheduledTaskRunRetentionService(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        DateTimeOffset cutoff = _clock.UtcNow.AddDays(-RetentionDays);
        return await ctx.AuditScheduledTaskRuns.Where(r => r.StartedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
