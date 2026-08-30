using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

/// <summary>Webhook 事件出站发射器实现（统一 fan-out）</summary>
/// <remarks>
/// 收口原 ArchiveService / BackupService 各自内联的「查 Enabled 订阅 → 过滤 Events.Contains → 写 Delivery → 入 Outbox」。
/// 受 Webhook 总开关 gated；自身异常仅吞咽记 Warning，绝不阻断触发它的主流程。
/// Events 是 JSON 列、EF 无法把 Contains 翻成 SQL，故先取回 Enabled 订阅再内存过滤（与既有生产者一致）。
/// RequestId 同时写入 Delivery 行与 payload.requestId，便于跨日志追踪；SaveChanges 后从 Local 重定位主键入队。
/// </remarks>
internal sealed class WebhookEmitter : IWebhookEmitter
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IWebhookOutboxQueue _outboxQueue;
    private readonly IClock _clock;
    private readonly ILogger<WebhookEmitter> _logger;

    public WebhookEmitter(
        IDbContextFactory<PmmDbContext> dbFactory,
        IWebhookOutboxQueue outboxQueue,
        IClock clock,
        ILogger<WebhookEmitter> logger)
    {
        _dbFactory = dbFactory;
        _outboxQueue = outboxQueue;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>负载 JSON 配置（camelCase + CJK 不转义，保中文原样可读）</summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task EmitAsync(string @event, object data, CancellationToken ct = default)
    {
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

            // Webhook 总开关（默认关）：关闭时不查订阅、不产生任何投递记录
            string? enabledRaw = await db.SystemSettings.AsNoTracking()
                .Where(s => s.Key == WebhookSubscriptionService.GlobalEnabledKey)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            if (!string.Equals(enabledRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return;

            List<WebhookSubscription> subs = await db.WebhookSubscriptions
                .Where(s => s.Enabled)
                .ToListAsync(ct);
            subs = subs.Where(s => s.Events.Contains(@event)).ToList();
            if (subs.Count == 0) return;

            string requestId = Guid.NewGuid().ToString("N");
            string payload = JsonSerializer.Serialize(new
            {
                @event,
                requestId,
                occurredAt = _clock.UtcNow,
                data,
            }, PayloadJsonOptions);

            foreach (WebhookSubscription sub in subs)
            {
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    SubscriptionId = sub.Id,
                    Event = @event,
                    Payload = payload,
                    Status = WebhookDeliveryStatus.Pending,
                    Attempts = 0,
                    RequestId = requestId,
                });
            }
            await db.SaveChangesAsync(ct);

            // SaveChanges 后 Id 已填，从 ChangeTracker.Local 按 (SubscriptionId, RequestId) 重定位入队
            foreach (WebhookSubscription sub in subs)
            {
                long deliveryId = db.WebhookDeliveries.Local
                    .Where(d => d.SubscriptionId == sub.Id && d.RequestId == requestId)
                    .Select(d => d.Id).Single();
                await _outboxQueue.EnqueueAsync(deliveryId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送 Webhook 事件 {Event} 失败（不影响主流程）", @event);
        }
    }
}
