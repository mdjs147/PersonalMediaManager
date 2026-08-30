using Microsoft.Extensions.Logging;
using Quartz;
using PersonalMediaManager.Application.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

/// <summary>AiCallRetentionJob（监控增强）— 每天清理超期 / 超量的 Audit_AiCall 调用日志</summary>
/// <remarks>
/// 与 LogRetentionJob 同构（文件 vs DB 行）：删除逻辑在 <see cref="IAiCallRetentionService"/>（Persistence 实现），
/// 本类仅负责调度触发 + 经 IScheduledTaskRunRecorder 落 Audit_ScheduledTaskRun。
/// 双闸（保留天数 + 单 provider 行数上限）读 System_Setting，防请求/响应原文累积撑爆 SQLite。
/// 异常由 Recorder 吞咽只记 Failed（周期任务靠下一轮兜底，不走 Quartz misfire），与 LogRetentionJob 一致。
/// </remarks>
[DisallowConcurrentExecution]
public sealed class AiCallRetentionJob : IJob
{
    public static readonly JobKey Key = new("aicall-retention", "maintenance");

    private readonly IAiCallRetentionService _service;
    private readonly IScheduledTaskRunRecorder _recorder;
    private readonly ILogger<AiCallRetentionJob> _logger;

    public AiCallRetentionJob(IAiCallRetentionService service, IScheduledTaskRunRecorder recorder, ILogger<AiCallRetentionJob> logger)
    {
        _service = service;
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
                int deleted = await _service.PurgeAsync(ctx.CancellationToken);
                ctx.WithProcessed(deleted);
                _logger.LogInformation("AiCallRetentionJob 完成：清理 {Deleted} 行 Audit_AiCall", deleted);
            },
            ct: context.CancellationToken);
    }
}
