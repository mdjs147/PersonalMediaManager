using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

/// <summary>Audit_ScheduledTaskRun 写入实现</summary>
/// <remarks>
/// 默认模式双 DbContext 写入：进入时一个 ctx，退出时另一个 ctx（避免 Job 业务跑久了第一个 ctx tracking 状态过期）；
/// skipAuditWhenIdle=true 走延迟写入：执行完才决定落不落库，空转（无失败且 Processed 为 0/null）不留行，
/// 需要落库时一次性 INSERT 完整终态行（高频 Job 防刷屏 + 防写放大，详见接口 remarks）。
/// 任一阶段写入失败仅记 Warning，不抛（业务成功 + 审计失败 ≠ 业务失败）。
/// </remarks>
internal sealed class ScheduledTaskRunRecorder : IScheduledTaskRunRecorder
{
    private const int ErrorMessageMaxLength = 2000;
    private const int DetailJsonMaxLength = 2000;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly ILogger<ScheduledTaskRunRecorder> _logger;

    public ScheduledTaskRunRecorder(
        IDbContextFactory<PmmDbContext> dbFactory,
        IClock clock,
        ILogger<ScheduledTaskRunRecorder> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(
        string jobKey,
        string? fireInstanceId,
        Func<ScheduledTaskRunContext, Task> body,
        CancellationToken ct,
        bool skipAuditWhenIdle = false)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
            throw new ArgumentException("jobKey 不能为空", nameof(jobKey));

        if (skipAuditWhenIdle)
        {
            await RunDeferredAsync(jobKey, fireInstanceId, body, ct);
            return;
        }

        DateTimeOffset startedAt = _clock.UtcNow;
        // 起始行写入用 None：即使 Job 进入时 token 已被取消，也要落 Running 行供 CompleteAsync 改写 Canceled，保留审计痕迹
        long runId = await InsertRunningAsync(jobKey, fireInstanceId, startedAt);

        ScheduledTaskRunContext jobCtx = new(ct);
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            await body(jobCtx);
            sw.Stop();
            await CompleteAsync(runId, ScheduledTaskOutcome.Succeeded, sw.ElapsedMilliseconds, jobCtx.Processed, jobCtx.Detail, errorMessage: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            await CompleteAsync(runId, ScheduledTaskOutcome.Canceled, sw.ElapsedMilliseconds, jobCtx.Processed, jobCtx.Detail, errorMessage: "Job 被取消");
            // 与现有三个 Job（FullScan / WebhookRetry / LogRetention）契约一致：取消不向 Quartz 上抛，
            // 避免污染 misfire 策略；审计行已标 Canceled，业务侧 cancellation 信号已透传给 lambda 内部。
        }
        catch (Exception ex)
        {
            sw.Stop();
            string msg = Truncate(ex.Message, ErrorMessageMaxLength) ?? "未知异常";
            await CompleteAsync(runId, ScheduledTaskOutcome.Failed, sw.ElapsedMilliseconds, jobCtx.Processed, jobCtx.Detail, errorMessage: msg);
            // 与 FullScanJob/LogRetentionJob 现状一致：吞咽，靠下一轮兜底
            _logger.LogError(ex, "Job {JobKey} 执行失败（RunId={RunId}）", jobKey, runId);
        }
    }

    /// <summary>延迟写入路径：执行完才决定落不落库（skipAuditWhenIdle=true 专用）</summary>
    /// <remarks>
    /// 空转判定 =（Succeeded 或 Canceled）且 Processed 为 0/null —— 不留行；
    /// Failed 必落（r3.15 设计意图：DB 故障/业务异常必须可见，不能被静默吞掉）；
    /// Processed &gt; 0 时落完整 Succeeded/Canceled 行。异常吞咽契约与默认路径完全一致。
    /// </remarks>
    private async Task RunDeferredAsync(
        string jobKey,
        string? fireInstanceId,
        Func<ScheduledTaskRunContext, Task> body,
        CancellationToken ct)
    {
        DateTimeOffset startedAt = _clock.UtcNow;
        ScheduledTaskRunContext jobCtx = new(ct);
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            await body(jobCtx);
            sw.Stop();
            if ((jobCtx.Processed ?? 0) == 0) return; // 空转静默：不落库
            await InsertCompletedAsync(jobKey, fireInstanceId, startedAt, ScheduledTaskOutcome.Succeeded, sw.ElapsedMilliseconds, jobCtx.Processed, jobCtx.Detail, errorMessage: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            if ((jobCtx.Processed ?? 0) > 0)
            {
                await InsertCompletedAsync(jobKey, fireInstanceId, startedAt, ScheduledTaskOutcome.Canceled, sw.ElapsedMilliseconds, jobCtx.Processed, jobCtx.Detail, errorMessage: "Job 被取消");
            }
            // 与默认路径契约一致：取消不向 Quartz 上抛，避免污染 misfire 策略
        }
        catch (Exception ex)
        {
            sw.Stop();
            string msg = Truncate(ex.Message, ErrorMessageMaxLength) ?? "未知异常";
            await InsertCompletedAsync(jobKey, fireInstanceId, startedAt, ScheduledTaskOutcome.Failed, sw.ElapsedMilliseconds, jobCtx.Processed, jobCtx.Detail, errorMessage: msg);
            _logger.LogError(ex, "Job {JobKey} 执行失败（空转静默模式，单行落库）", jobKey);
        }
    }

    /// <summary>一次性 INSERT 完整终态行（延迟写入路径专用，无 Running 中间态）</summary>
    private async Task InsertCompletedAsync(string jobKey, string? fireInstanceId, DateTimeOffset startedAt, string outcome, long durationMs, int? processed, string? detail, string? errorMessage)
    {
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.AuditScheduledTaskRuns.Add(new AuditScheduledTaskRun
            {
                JobKey = jobKey,
                FireInstanceId = fireInstanceId,
                StartedAt = startedAt,
                FinishedAt = _clock.UtcNow,
                DurationMs = durationMs,
                Outcome = outcome,
                ProcessedCount = processed,
                ErrorMessage = errorMessage,
                DetailJson = Truncate(detail, DetailJsonMaxLength),
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit_ScheduledTaskRun 终态单行写入失败：JobKey={JobKey}", jobKey);
        }
    }

    private async Task<long> InsertRunningAsync(string jobKey, string? fireInstanceId, DateTimeOffset startedAt)
    {
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            AuditScheduledTaskRun run = new()
            {
                JobKey = jobKey,
                FireInstanceId = fireInstanceId,
                StartedAt = startedAt,
                Outcome = ScheduledTaskOutcome.Running,
            };
            db.AuditScheduledTaskRuns.Add(run);
            await db.SaveChangesAsync();
            return run.Id;
        }
        catch (Exception ex)
        {
            // 审计写入失败不阻塞业务：返回 0，CompleteAsync 内会跳过 update
            _logger.LogWarning(ex, "Audit_ScheduledTaskRun 起始行写入失败：JobKey={JobKey}", jobKey);
            return 0L;
        }
    }

    private async Task CompleteAsync(long runId, string outcome, long durationMs, int? processed, string? detail, string? errorMessage)
    {
        if (runId == 0L) return;
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            AuditScheduledTaskRun? run = await db.AuditScheduledTaskRuns
                .FirstOrDefaultAsync(r => r.Id == runId);
            if (run is null) return;
            run.FinishedAt = _clock.UtcNow;
            run.DurationMs = durationMs;
            run.Outcome = outcome;
            run.ProcessedCount = processed;
            run.ErrorMessage = errorMessage;
            run.DetailJson = Truncate(detail, DetailJsonMaxLength);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit_ScheduledTaskRun 终态行写入失败：RunId={RunId}", runId);
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null) return null;
        return value.Length <= max ? value : value[..max];
    }
}
