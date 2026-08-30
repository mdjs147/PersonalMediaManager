using Microsoft.Extensions.Logging;
using Quartz;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

/// <summary>TmdbZeroResultRetryJob — 周期重投「TMDB 未收录」待确认项</summary>
/// <remarks>
/// 驱动 ITmdbZeroResultRetrySweeper：把「规则高置信 + 多查询全零结果」被判定 TMDB 暂未收录的
/// AwaitingReview 记录按天重新排队（新番 / 新剧发布初期 TMDB 常滞后数日收录，收录后自动归档，
/// 全程零 AI 零人工）。「每天最多一次」由 Sweeper 的 UpdatedAt ≥ 20h 条件保证，本 Job 6 小时
/// 周期只决定「到期后多久被拾起」——比日切 Cron 更快救回、又不至于频繁扫库。
/// DisallowConcurrentExecution 防上一轮未完又触发；异常吞咽同 FullScanJob（周期任务靠下一轮兜底）；
/// skipAuditWhenIdle=true：空转不落 Audit_ScheduledTaskRun（多数轮次无到期记录，避免任务时间轴刷噪音）。
/// </remarks>
[DisallowConcurrentExecution]
public sealed class TmdbZeroResultRetryJob : IJob
{
    public static readonly JobKey Key = new("tmdb-zero-result-retry", "parse");

    private readonly ITmdbZeroResultRetrySweeper _sweeper;
    private readonly IScheduledTaskRunRecorder _recorder;
    private readonly ILogger<TmdbZeroResultRetryJob> _logger;

    public TmdbZeroResultRetryJob(
        ITmdbZeroResultRetrySweeper sweeper,
        IScheduledTaskRunRecorder recorder,
        ILogger<TmdbZeroResultRetryJob> logger)
    {
        _sweeper = sweeper;
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
                int requeued = await _sweeper.SweepAsync(context.CancellationToken);
                ctx.WithProcessed(requeued);
                if (requeued > 0)
                {
                    _logger.LogInformation("TmdbZeroResultRetryJob 重投 {Count} 条「TMDB 未收录」记录重新排队", requeued);
                }
            },
            ct: context.CancellationToken,
            skipAuditWhenIdle: true);
    }
}
