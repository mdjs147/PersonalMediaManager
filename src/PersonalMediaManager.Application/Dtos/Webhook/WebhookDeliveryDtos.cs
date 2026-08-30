using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Webhook;

/// <summary>Webhook 出站日志查询参数</summary>
public sealed record WebhookDeliveryQuery(
    long? SubscriptionId = null,
    WebhookDeliveryStatus? Status = null,
    int Skip = 0,
    int Take = 50);

public sealed record WebhookDeliveryResponse(
    long Id,
    long SubscriptionId,
    string Event,
    WebhookDeliveryStatus Status,
    int Attempts,
    DateTimeOffset? LastTriedAt,
    DateTimeOffset? NextRetryAt,
    int? LastStatusCode,
    string? LastError,
    string RequestId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WebhookDeliveryPage(
    IReadOnlyList<WebhookDeliveryResponse> Items,
    int Total);

/// <summary>手动重试返回（API 规范 §2.16.6：deliveryId / status / attempts）</summary>
public sealed record WebhookRetryResult(
    long DeliveryId,
    WebhookDeliveryStatus Status,
    int Attempts);
