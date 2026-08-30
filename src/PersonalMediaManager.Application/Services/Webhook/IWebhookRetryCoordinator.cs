namespace PersonalMediaManager.Application.Services.Webhook;

/// <summary>Webhook 重试调度协调器契约（被 WebhookRetryJob 周期触发）</summary>
/// <remarks>
/// 行为：扫描 Webhook_Delivery 中 Status ∈ {Pending, Retrying} 且 (NextRetryAt 为空 或 NextRetryAt ≤ now) 的记录，
/// 把它们重新入主出站 channel，由 WebhookOutboxWorker（D6.3）实际发送 + 退避（30s/2min/10min）。
/// 本接口只负责「唤醒」语义：保证补偿被进程重启、worker 漏单等异常吃掉的投递不会永远停在数据库里。
/// 实现：D6.3 / D7.x 完成；Platform.WebhookRetryJob 仅通过本接口跨越层界，避免反向引用 Persistence/Host。
/// </remarks>
public interface IWebhookRetryCoordinator
{
    /// <summary>扫一遍待重试投递并入队，返回本次拾起的条数</summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}
