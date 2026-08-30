using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Webhook;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

/// <summary>Webhook 出站日志实现（分页查询 + 手动重试）</summary>
/// <remarks>
/// 查询：Take 上限 200（避免一次拉太多日志影响 SQLite 性能）；Skip ≥ 0；按 CreatedAt 倒序；
/// 不返回 Payload 字段（敏感），如需排错由后端直接查 SQLite。
/// 重试：API 规范 §2.16.6——校验订阅/投递存在、归属匹配、未成功，重置后立即重新入队（详见 RetryAsync 注释）。
/// </remarks>
internal sealed class WebhookDeliveryService : IWebhookDeliveryService
{
    private const int MaxTake = 200;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IWebhookOutboxQueue _queue;
    private readonly IClock _clock;

    public WebhookDeliveryService(IDbContextFactory<PmmDbContext> dbFactory, IWebhookOutboxQueue queue, IClock clock)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _clock = clock;
    }

    public async Task<WebhookDeliveryPage> ListAsync(WebhookDeliveryQuery query, CancellationToken ct = default)
    {
        if (query.Skip < 0)
            throw new BusinessException("Skip 必须 ≥ 0");
        int take = query.Take <= 0 ? 50 : Math.Min(query.Take, MaxTake);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<WebhookDelivery> q = ctx.WebhookDeliveries.AsNoTracking();
        if (query.SubscriptionId is not null)
            q = q.Where(d => d.SubscriptionId == query.SubscriptionId.Value);
        if (query.Status is not null)
            q = q.Where(d => d.Status == query.Status.Value);

        int total = await q.CountAsync(ct);
        // Webhook_Delivery.Id 自增 = 创建时间单调递增；按 Id 倒序 = 按 CreatedAt 倒序，
        // 且复用 PK 索引避免在 CreatedAt/UpdatedAt 上额外建索引（YAGNI）
        List<WebhookDelivery> rows = await q
            .OrderByDescending(d => d.Id)
            .Skip(query.Skip).Take(take)
            .ToListAsync(ct);

        IReadOnlyList<WebhookDeliveryResponse> items = rows.Select(d => new WebhookDeliveryResponse(
            d.Id, d.SubscriptionId, d.Event, d.Status, d.Attempts,
            d.LastTriedAt, d.NextRetryAt, d.LastStatusCode, d.LastError,
            d.RequestId, d.CreatedAt, d.UpdatedAt)).ToList();
        return new WebhookDeliveryPage(items, total);
    }

    /// <summary>手动重试：非 Success 投递重置为 Pending + 立即重新入队（API 规范 §2.16.6）</summary>
    /// <remarks>
    /// 校验顺序：订阅存在 → 投递存在 → 归属匹配 → 非 Success（Failed / Retrying / Pending 卡死均可重试）。
    /// 重置策略：API 规范未定义 Attempts 处理 → 归零，让投递重新获得完整「首发 + 3 次退避重试」链
    ///   （而非只补一次尝试）；LastError / LastStatusCode 保留上一轮失败现场便于排错。
    /// NextRetryAt = now：即使下方主动入队失败，WebhookRetryJob（1 分钟周期）扫描也会兜底拾起。
    /// EnqueueAsync 异常不捕获 → ExceptionHandlerMiddleware 归 9000（重试入队失败），但 DB 状态已落地不会真丢。
    /// </remarks>
    public async Task<WebhookRetryResult> RetryAsync(long subscriptionId, long deliveryId, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        bool subExists = await ctx.WebhookSubscriptions.AsNoTracking().AnyAsync(s => s.Id == subscriptionId, ct);
        if (!subExists)
            throw new BusinessException("订阅不存在");

        WebhookDelivery? delivery = await ctx.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null)
            throw new BusinessException("投递记录不存在");
        if (delivery.SubscriptionId != subscriptionId)
            throw new BusinessException("投递记录归属不匹配");
        if (delivery.Status == WebhookDeliveryStatus.Success)
            throw new BusinessException("投递记录已成功，无需重试");

        delivery.Status = WebhookDeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextRetryAt = _clock.UtcNow;
        await ctx.SaveChangesAsync(ct);

        await _queue.EnqueueAsync(deliveryId, ct);

        return new WebhookRetryResult(delivery.Id, delivery.Status, delivery.Attempts);
    }
}
