namespace PersonalMediaManager.Application.Services.Audit;

/// <summary>Quartz Job 执行记录器（Audit_ScheduledTaskRun 写入入口）</summary>
/// <remarks>
/// 设计目标：把「INSERT Running → 执行业务 → UPDATE 终态 + 异常吞咽」三段重复模板抽掉，
/// Job 实现只关心业务，调度审计自动落库。配合 Dashboard /dashboard/tasks 24h 时间轴查询。
///
/// 使用示例（在 IJob.Execute 内）：
/// <code>
/// await _recorder.RunAsync("scan.full-scan", context.FireInstanceId, async ctx =>
/// {
///     int picked = await _coordinator.RunAsync(ctx.CancellationToken);
///     ctx.WithProcessed(picked);
/// }, context.CancellationToken);
/// </code>
///
/// 异常策略：
/// - 业务 lambda 抛 OperationCanceledException + token cancelled → Outcome=Canceled，吞咽不抛（避免污染 Quartz misfire 策略）
/// - 业务 lambda 抛其他异常 → Outcome=Failed + ErrorMessage = ex.Message，吞咽不抛（保持 Quartz Job 异常吃掉的现状）
/// - 正常返回 → Outcome=Succeeded + ProcessedCount/DetailJson 走 ctx.With* 链
/// 任一阶段 DB 写失败仅记 Warning，绝不掩盖业务结果（业务成功 + 审计失败 ≠ 业务失败）。
///
/// skipAuditWhenIdle（高频空转 Job 专用，如 1 分钟周期的 WebhookRetryJob）：
/// - true 时改为「延迟写入」：执行完才决定落不落库——(Succeeded 或 Canceled) 且 Processed 为 0/null 视为空转，
///   不写任何行（避免每分钟两次 SaveChanges 把 Audit_ScheduledTaskRun 刷爆、Dashboard 时间轴被空转记录刷屏）；
///   Failed 或 Processed &gt; 0 时一次性 INSERT 完整终态行（无 Running 中间态）。
/// - 代价：该模式下进程崩溃不留 Running 残痕——对秒级空扫 Job 可接受；长任务（FullScan 等）保持默认 false 不受影响。
/// </remarks>
public interface IScheduledTaskRunRecorder
{
    /// <summary>以 Audit_ScheduledTaskRun 包裹一段 Job 业务的执行</summary>
    Task RunAsync(
        string jobKey,
        string? fireInstanceId,
        Func<ScheduledTaskRunContext, Task> body,
        CancellationToken ct,
        bool skipAuditWhenIdle = false);
}

/// <summary>Job 业务 lambda 内可设置的附加字段</summary>
/// <remarks>
/// 提供 fluent 风格 With* 方法在业务执行中累积；token 透传给 lambda 内被取消感知的子流程使用。
/// </remarks>
public sealed class ScheduledTaskRunContext
{
    /// <summary>当前 Job 的 cancellation token（透传自 Quartz IJobExecutionContext.CancellationToken）</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>WithProcessed 累积的处理量；Recorder 读取后落 Audit_ScheduledTaskRun.ProcessedCount</summary>
    public int? Processed { get; private set; }

    /// <summary>WithDetail 累积的上下文 JSON；Recorder 读取后落 Audit_ScheduledTaskRun.DetailJson</summary>
    public string? Detail { get; private set; }

    /// <summary>由 Recorder 实现侧构造，业务侧通过 RunAsync 的 lambda 入参拿到本实例</summary>
    public ScheduledTaskRunContext(CancellationToken ct) { CancellationToken = ct; }

    /// <summary>记 Job 自报处理量（如 FullScan 入队数 / WebhookRetry 拾起数 / LogRetention 删除数）</summary>
    public ScheduledTaskRunContext WithProcessed(int count)
    {
        Processed = count;
        return this;
    }

    /// <summary>记附加上下文 JSON（target / note 等，长度 ≤ 2000）</summary>
    public ScheduledTaskRunContext WithDetail(string detailJson)
    {
        Detail = detailJson;
        return this;
    }
}
