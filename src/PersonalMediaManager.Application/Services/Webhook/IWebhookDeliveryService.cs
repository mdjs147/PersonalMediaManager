using PersonalMediaManager.Application.Dtos.Webhook;

namespace PersonalMediaManager.Application.Services.Webhook;

/// <summary>Webhook 出站日志契约（分页查询 + 手动重试）</summary>
/// <remarks>
/// 不暴露 Payload（避免日志中带敏感字段），需排错可由后端直接看 SQLite。
/// 排序：按 CreatedAt 倒序；分页用 Skip/Take，Take 上限 200。
/// RetryAsync：API 规范 §2.16.6 手动重试——把自动退避耗尽后 Failed（或卡死的 Pending/Retrying）投递重置并立即重新入队。
/// </remarks>
public interface IWebhookDeliveryService
{
    Task<WebhookDeliveryPage> ListAsync(WebhookDeliveryQuery query, CancellationToken ct = default);

    /// <summary>手动重试：非 Success 投递重置为 Pending + 立即入队（订阅/投递不存在、归属不匹配、已成功均抛业务异常）</summary>
    Task<WebhookRetryResult> RetryAsync(long subscriptionId, long deliveryId, CancellationToken ct = default);
}
