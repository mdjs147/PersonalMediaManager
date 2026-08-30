using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>Webhook 出站日志（Webhook_Delivery）</summary>
/// <remarks>
/// CASCADE 关联 WebhookSubscription；WebhookOutboxWorker 按 NextRetryAt + Status 扫描派发。
/// Status 流转：Pending → (HTTP 2xx) Success | (失败) Retrying → ... → 首发 + 3 次重试（退避 30s/2min/10min）耗尽后 Failed；
/// Failed 可经手动重试端点（POST settings/webhooks/{id}/retry/{deliveryId}）重置回 Pending 重新派发。
/// RequestId 与 payload.requestId 一致，便于跨日志追踪。
/// </remarks>
public sealed class WebhookDelivery : Entity
{
    public long SubscriptionId { get; set; }

    /// <summary>事件名（如 media.archived）</summary>
    public string Event { get; set; } = default!;

    /// <summary>JSON 请求体</summary>
    public string Payload { get; set; } = default!;

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset? LastTriedAt { get; set; }

    /// <summary>下一次重试时间；NULL 表示不再重试</summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }

    /// <summary>追踪 ID（与 payload.requestId 一致）</summary>
    public string RequestId { get; set; } = default!;
}
