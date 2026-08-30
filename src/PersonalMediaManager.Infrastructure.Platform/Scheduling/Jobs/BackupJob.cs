using Microsoft.Extensions.Logging;
using Quartz;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.System;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

/// <summary>BackupJob — 每天定时执行数据库在线快照备份（VACUUM INTO + zip 含密钥环）+ 按份数清理</summary>
/// <remarks>
/// 与 AiCallRetentionJob 同构：备份 + 清理逻辑在 <see cref="IBackupService"/>（Persistence 实现，读 Backup_Enabled / Backup_RetainCount），
/// 本类仅负责调度触发 + 经 IScheduledTaskRunRecorder 落 Audit_ScheduledTaskRun。
/// 启用开关 Backup_Enabled 由 RunScheduledAsync 内部判定（停用则 Performed=false、不产物）。
/// 异常由 Recorder 吞咽只记 Failed，周期任务靠下一轮兜底（不走 Quartz misfire），与 LogRetentionJob 一致。
/// </remarks>
[DisallowConcurrentExecution]
public sealed class BackupJob : IJob
{
    public static readonly JobKey Key = new("backup", "maintenance");

    private readonly IBackupService _service;
    private readonly IScheduledTaskRunRecorder _recorder;
    private readonly ILogger<BackupJob> _logger;

    public BackupJob(IBackupService service, IScheduledTaskRunRecorder recorder, ILogger<BackupJob> logger)
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
                BackupResult result = await _service.RunScheduledAsync(ctx.CancellationToken);
                ctx.WithProcessed(result.Performed ? 1 : 0);
                if (result.Performed)
                    _logger.LogInformation("BackupJob 完成：{File}（{Size} 字节，清理旧备份 {Pruned} 份）",
                        result.FileName, result.SizeBytes, result.PrunedCount);
                else
                    _logger.LogDebug("BackupJob：自动备份未启用，跳过");
            },
            ct: context.CancellationToken);
    }
}
