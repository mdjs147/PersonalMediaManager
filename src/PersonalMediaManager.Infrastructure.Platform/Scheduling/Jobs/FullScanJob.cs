using Microsoft.Extensions.Logging;
using Quartz;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Scan;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

/// <summary>FullScanJob（D5.2）— 12 小时周期全量扫描所有启用监控目录</summary>
/// <remarks>
/// 仅作为 Quartz 触发入口，业务交给 Application 层 IFullScanCoordinator（D7.6 实现）；
/// DisallowConcurrentExecution 保证上次未跑完不重复触发，避免 12h 周期下扫描堆积；
/// PersistJobDataAfterExecution 在 Quartz In-Memory 场景下无副作用，保留语义以备后续切换持久化。
/// 失败处理：仅记 Error 日志，不抛 JobExecutionException——Quartz 默认行为已是「单次失败不影响下次触发」，
/// 抛出会污染 misfire 策略；FullScan 是补偿性任务，下次 12h 会再来一遍，不需要立即重试。
/// </remarks>
[DisallowConcurrentExecution]
[PersistJobDataAfterExecution]
public sealed class FullScanJob : IJob
{
    /// <summary>Job 唯一标识（Configurator + 测试共用）</summary>
    public static readonly JobKey Key = new("full-scan", "scan");

    private readonly IFullScanCoordinator _coordinator;
    private readonly IScheduledTaskRunRecorder _recorder;
    private readonly ILogger<FullScanJob> _logger;

    public FullScanJob(IFullScanCoordinator coordinator, IScheduledTaskRunRecorder recorder, ILogger<FullScanJob> logger)
    {
        _coordinator = coordinator;
        _recorder = recorder;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("FullScanJob 启动：FireInstanceId={FireInstanceId}", context.FireInstanceId);
        return _recorder.RunAsync(
            jobKey: $"{Key.Group}.{Key.Name}",
            fireInstanceId: context.FireInstanceId,
            body: async _ =>
            {
                await _coordinator.RunAsync(context.CancellationToken);
                _logger.LogInformation("FullScanJob 完成：FireInstanceId={FireInstanceId}", context.FireInstanceId);
            },
            ct: context.CancellationToken);
    }
}
