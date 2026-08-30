using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

/// <summary>Webhook 重试调度协调器实现（被 WebhookRetryJob 周期调用）</summary>
/// <remarks>
/// 扫描策略：取 Status ∈ {Pending, Retrying} 且 (NextRetryAt 为空 或 NextRetryAt ≤ now) 的 Webhook_Delivery 行，
/// 逐条 await IWebhookOutboxQueue.EnqueueAsync 入主出站 channel。
/// 仅负责唤醒；不写状态、不发 HTTP；具体发送 + 退避由 WebhookOutboxWorker 处理。
///
/// DbContext 隔离：
///   按 §八 红线，独立 DbContext，避免与并行运行的其他业务公用一个上下文。
///   仅做一次只读 ToListAsync 扫描，关闭即可。
///
/// 上限保护：
///   单次扫描最多 MaxBatch=200 条，防止数据库历史堆积时一次性把 channel（容量 4096）灌满阻塞。
///   未处理完的行下一轮（1 分钟周期）继续拾起。
///
/// 异常吞咽：
///   单条 EnqueueAsync 失败仅记 Warning，继续下一条；外层异常由 WebhookRetryJob.ScheduledTaskRunRecorder 兜底落 Audit。
/// </remarks>
internal sealed class WebhookRetryCoordinator : IWebhookRetryCoordinator
{
    /// <summary>单次扫描最多拾起条数（容量保护）</summary>
    private const int MaxBatch = 200;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IWebhookOutboxQueue _queue;
    private readonly ILogger<WebhookRetryCoordinator> _logger;

    public WebhookRetryCoordinator(
        IDbContextFactory<PmmDbContext> dbFactory,
        IWebhookOutboxQueue queue,
        ILogger<WebhookRetryCoordinator> logger)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // P1-r3.15：DB 查询单独 try/catch，明确「DB 故障」与「业务空集」两条路径
        // 旧实现两种情况均返 0 → Recorder 记 Succeeded → OPS 无法区分「连续多轮无投递」是真没数据还是 DB 异常
        List<long> ids;
        try
        {
            // 只查主键：避免拉 Payload 大字段；按 Id 升序处理（先入库的先重试）
            ids = await db.WebhookDeliveries
                .AsNoTracking()
                .Where(d =>
                    (d.Status == WebhookDeliveryStatus.Pending || d.Status == WebhookDeliveryStatus.Retrying)
                    && (d.NextRetryAt == null || d.NextRetryAt <= now))
                .OrderBy(d => d.Id)
                .Take(MaxBatch)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停机：透传给上层让 Recorder 记 Canceled
            throw;
        }
        catch (Exception ex)
        {
            // DB 查询异常：明确以 Error 级别区别于「业务空集」的 Debug 日志，
            // 重新抛出让 IScheduledTaskRunRecorder 记 Failed + ErrorMessage（避免静默记 Succeeded）
            _logger.LogError(ex, "WebhookRetry 扫描 DB 查询失败（DB 故障，区别于业务空集 picked=0）");
            throw;
        }

        if (ids.Count == 0)
        {
            // 真正业务空集：Debug 级日志，让 OPS 对照「同时无 Error 日志 + picked=0」=「DB OK 且无待重试」
            _logger.LogDebug("WebhookRetry 扫描：无待重试投递（业务空集，非 DB 异常）");
            return 0;
        }

        int picked = 0;
        foreach (long id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _queue.EnqueueAsync(id, cancellationToken);
                picked++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 正常停机：把已处理数返回，上层 Recorder 仍可记 Success
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Webhook 重试入队失败：DeliveryId={DeliveryId}", id);
            }
        }

        return picked;
    }
}
