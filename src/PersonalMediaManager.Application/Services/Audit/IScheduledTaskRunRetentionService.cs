namespace PersonalMediaManager.Application.Services.Audit;

/// <summary>Audit_ScheduledTaskRun 留存清理 — 删除超过保留期的调度运行记录</summary>
/// <remarks>
/// 被 LogRetentionJob 每日触发（与文件日志清理同班次：调度运行记录本质也是一种日志）。
/// 固定保留 30 天（与日志文件保留期一致），不走 System_Setting：
/// 该表在 WebhookRetryJob 空转静默后日增仅个位数行，无配置面的必要。
/// 进程崩溃遗留的 Outcome=Running 僵尸行同样按 StartedAt 天龄覆盖，无需特判。
/// 返回删除行数。
/// </remarks>
public interface IScheduledTaskRunRetentionService
{
    /// <summary>执行一次按天龄清理，返回删除的行数</summary>
    Task<int> PurgeAsync(CancellationToken ct = default);
}
