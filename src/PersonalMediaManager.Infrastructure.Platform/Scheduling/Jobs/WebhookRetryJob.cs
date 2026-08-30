using Microsoft.Extensions.Logging;
using Quartz;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Webhook;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

/// <summary>WebhookRetryJob（D5.4）— 周期扫描 Pending / Retrying 投递并入队</summary>
/// <remarks>
/// 与 WebhookOutboxWorker（D6.3）职责切分：
///   OutboxWorker 负责实际 HTTP 发送 + 30s/2min/10min 退避；
///   本 Job 只做「兜底唤醒」：进程重启 / channel 漏单 / NextRetryAt 已过仍未触发等异常场景，
///   保证不会有投递永久停在数据库 Pending 状态。
/// 1 分钟周期：与最短重试窗口 30s 同量级，又不至于把 DB 扫穿；
/// DisallowConcurrentExecution 防上一次未跑完又被触发造成重复入队；
/// 异常吞咽同 FullScanJob / LogRetentionJob：周期任务靠下一轮兜底。
/// skipAuditWhenIdle=true：空转（无待重试投递）不落 Audit_ScheduledTaskRun——
/// 否则每分钟两次 SaveChanges 把表刷成每年 50 万行、Dashboard 任务时间轴全是「处理 0 个」噪音；
/// 拾起 &gt; 0 或失败时照常落行（失败可见性不受影响）。
/// </remarks>
[DisallowConcurrentExecution]
public sealed class WebhookRetryJob : IJob
{
    public static readonly JobKey Key = new("webhook-retry", "webhook");

    private readonly IWebhookRetryCoordinator _coordinator;
    private readonly IScheduledTaskRunRecorder _recorder;
    private readonly ILogger<WebhookRetryJob> _logger;

    public WebhookRetryJob(IWebhookRetryCoordinator coordinator, IScheduledTaskRunRecorder recorder, ILogger<WebhookRetryJob> logger)
    {
        _coordinator = coordinator;
        _recorder = recorder;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context)
    {
        return _recorder.RunAsync(
            jobKey: $"{Key.Group}.{Key.Name}",
            fireInstanceId: context.FireInstanceId,
            body: async ctx =>
            {
                int picked = await _coordinator.SweepAsync(context.CancellationToken);
                ctx.WithProcessed(picked);
                if (picked > 0)
                {
                    _logger.LogInformation("WebhookRetryJob 拾起 {Picked} 条待重试投递入队", picked);
                }
            },
            ct: context.CancellationToken,
            skipAuditWhenIdle: true);
    }
}
