using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Webhook;

namespace PersonalMediaManager.Application.Services.Webhook;

/// <summary>Webhook 订阅服务契约（C.7：CRUD + 测试发送；出站留 D 阶段 OutboxWorker）</summary>
/// <remarks>
/// Secret 走 IProtectedFieldService 加密（HMAC-SHA256 签名密钥）；
/// Events 列存 JSON 数组；同 Name 唯一；删除 CASCADE 清出站日志（DB FK 约束）。
/// TestAsync：同步向 Subscription.Url 发送一次 test.ping 事件（与生产同形 HMAC + 头部），不入 Webhook_Delivery 表。
/// </remarks>
public interface IWebhookSubscriptionService
{
    Task<IReadOnlyList<WebhookSubscriptionResponse>> ListAsync(CancellationToken ct = default);

    Task<WebhookSubscriptionResponse> CreateAsync(CreateWebhookSubscriptionRequest req, CancellationToken ct = default);

    Task<WebhookSubscriptionResponse> UpdateAsync(UpdateWebhookSubscriptionRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteWebhookSubscriptionRequest req, CancellationToken ct = default);

    Task<WebhookTestResult> TestAsync(long id, CancellationToken ct = default);

    /// <summary>读 Webhook 总开关（System_Setting.Webhook_Enabled；默认 false=关闭）</summary>
    Task<bool> GetGlobalEnabledAsync(CancellationToken ct = default);

    /// <summary>设置 Webhook 总开关（关闭时归档不再产生 Webhook_Delivery；缺行时兜底 upsert）</summary>
    Task SetGlobalEnabledAsync(bool enabled, CancellationToken ct = default);
}
