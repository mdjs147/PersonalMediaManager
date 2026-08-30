using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>调度任务执行记录（Audit_ScheduledTaskRun）</summary>
/// <remarks>
/// 与 <see cref="AuditOperation"/> / <see cref="AuditAiCall"/> 同语义：过去发生事件的只读追加表。
/// 每次 Quartz Job 触发由 IScheduledTaskRunRecorder.RunAsync 包裹执行：
///   - 进入时 INSERT 一行 Outcome=Running + StartedAt + FireInstanceId；
///   - 退出时 UPDATE 同一行 Outcome=Succeeded/Failed/Canceled + FinishedAt + DurationMs + ProcessedCount + ErrorMessage；
///   - 高频空转 Job（WebhookRetryJob）走 skipAuditWhenIdle 延迟写入：空转不留行，需要落库时单次 INSERT 完整终态行。
/// 索引 (StartedAt DESC) 支撑 Dashboard 24h 任务时间轴查询；(JobKey, StartedAt DESC) 支撑按 Job 过滤。
/// Outcome=Running 行表示「该任务尚未完成」；超期行（含进程崩溃留下的 Running 僵尸行）由
/// LogRetentionJob 经 IScheduledTaskRunRetentionService 按 30 天天龄清理。
/// </remarks>
public sealed class AuditScheduledTaskRun : Entity
{
    /// <summary>Quartz JobKey 字符串形式（group.name，如 scan.full-scan）</summary>
    public string JobKey { get; set; } = default!;

    /// <summary>Quartz 单次触发实例 ID（context.FireInstanceId），便于与日志 grep 对齐</summary>
    public string? FireInstanceId { get; set; }

    /// <summary>开始时刻（UTC）</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>完成时刻（UTC，Running 状态为 null）</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>耗时毫秒（FinishedAt - StartedAt，Running 状态为 null）</summary>
    public long? DurationMs { get; set; }

    /// <summary>执行结果（Running / Succeeded / Failed / Canceled）</summary>
    public string Outcome { get; set; } = ScheduledTaskOutcome.Running;

    /// <summary>Job 自报的处理量（如 FullScan 入队文件数 / WebhookRetry 重试条数 / LogRetention 删除数）</summary>
    public int? ProcessedCount { get; set; }

    /// <summary>失败摘要（Outcome=Failed 时写）</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>附加上下文 JSON（target / note 等灵活字段），可为 null</summary>
    public string? DetailJson { get; set; }
}

/// <summary>调度任务执行结果常量（字符串避免枚举值漂移）</summary>
public static class ScheduledTaskOutcome
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
}
