using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;

/// <summary>Webhook 订阅聚合根（Webhook_Subscription）</summary>
/// <remarks>
/// Secret 走 DataProtection 加密；HMAC-SHA256 签名头由 WebhookOutboxWorker（D6.3）生成。
/// Events 列存 JSON 数组（如 ["media.archived","media.failed"]），EF ValueConverter（A4.5）。
/// CASCADE 关联 Webhook_Delivery：删除订阅时同时清出站日志。
/// </remarks>
public sealed class WebhookSubscription : AggregateRoot
{
    public string Name { get; set; } = default!;

    public string Url { get; set; } = default!;

    /// <summary>DataProtection 加密；HMAC 签名密钥</summary>
    public string? SecretEncrypted { get; set; }

    /// <summary>JSON 数组：订阅的事件名</summary>
    public List<string> Events { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 10;
}
