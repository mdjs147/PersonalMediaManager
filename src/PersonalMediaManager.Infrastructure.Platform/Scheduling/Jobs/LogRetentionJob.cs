using Microsoft.Extensions.Logging;
using Quartz;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

/// <summary>LogRetentionJob（D5.3）— 每天清理超出保留期的日志文件 + 调度运行记录</summary>
/// <remarks>
/// 需求文档 §3.11：日志按天滚动（pmm-yyyyMMdd.log）+ 单文件 10MB 强制切割，保留 30 天。
/// 删除依据：LastWriteTimeUtc &lt; 截止时刻；不解析文件名（避免被 10MB 切割产物 pmm-yyyyMMdd_NNN.log 漏掉）。
/// 文件名通配 pmm-*.log：仅清理 PMM 自己的日志，绝不误删用户放在 LogDir 的其他文件。
/// 单文件失败（占用 / 权限）只记 Warning 不抛——保证当日清理批量推进，明天 Cron 再补；
/// 整体异常也只记 Error 不抛，原因同 FullScanJob：周期任务靠下一轮兜底，不走 Quartz misfire。
/// 第二步顺带清 Audit_ScheduledTaskRun 超期行（IScheduledTaskRunRetentionService，30 天）：
/// 调度运行记录本质也是日志，此前无任何自动清理路径会无限增长；
/// 顺序先文件后 DB，文件删除数先 WithProcessed 落 ctx——DB 清理若抛异常，Failed 行仍保留文件清理量。
/// </remarks>
[DisallowConcurrentExecution]
public sealed class LogRetentionJob : IJob
{
    public static readonly JobKey Key = new("log-retention", "maintenance");

    /// <summary>默认保留 30 天</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    /// <summary>清理目标的文件通配</summary>
    public const string LogFilePattern = "pmm-*.log";

    private readonly AppPaths _paths;
    private readonly IClock _clock;
    private readonly IScheduledTaskRunRetentionService _taskRunRetention;
    private readonly IScheduledTaskRunRecorder _recorder;
    private readonly ILogger<LogRetentionJob> _logger;

    public LogRetentionJob(AppPaths paths, IClock clock, IScheduledTaskRunRetentionService taskRunRetention, IScheduledTaskRunRecorder recorder, ILogger<LogRetentionJob> logger)
    {
        _paths = paths;
        _clock = clock;
        _taskRunRetention = taskRunRetention;
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
                int deletedFiles = PurgeOlderThan(_paths.LogDir, DefaultRetention);
                ctx.WithProcessed(deletedFiles); // 先落文件数：DB 清理抛异常时 Failed 行仍保留文件清理量
                int deletedRuns = await _taskRunRetention.PurgeAsync(ctx.CancellationToken);
                ctx.WithProcessed(deletedFiles + deletedRuns)
                   .WithDetail($$"""{"logFiles":{{deletedFiles}},"taskRuns":{{deletedRuns}}}""");
                _logger.LogInformation("LogRetentionJob 完成：删除 {Files} 个日志文件 + {Runs} 行调度运行记录，目录={LogDir}", deletedFiles, deletedRuns, _paths.LogDir);
            },
            ct: context.CancellationToken);
    }

    /// <summary>清理指定目录下 pmm-*.log 中 LastWriteTimeUtc 超出保留期的文件，返回删除数</summary>
    /// <remarks>internal 以便测试直连验证；Quartz Execute 也走它。目录不存在直接返回 0。</remarks>
    internal int PurgeOlderThan(string logDir, TimeSpan retention)
    {
        if (!Directory.Exists(logDir))
        {
            return 0;
        }

        DateTimeOffset cutoff = _clock.UtcNow - retention;
        int deleted = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(logDir, LogFilePattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "枚举日志目录失败：{LogDir}", logDir);
            return 0;
        }

        foreach (string file in files)
        {
            try
            {
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(file);
                if (lastWriteUtc >= cutoff.UtcDateTime)
                {
                    continue;
                }
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除日志文件失败（跳过）：{File}", file);
            }
        }

        return deleted;
    }
}
