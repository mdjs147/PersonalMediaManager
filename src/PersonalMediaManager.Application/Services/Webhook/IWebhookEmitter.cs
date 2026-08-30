namespace PersonalMediaManager.Application.Services.Webhook;

/// <summary>Webhook 事件出站发射器</summary>
/// <remarks>
/// 统一入口：把一个事件 fan-out 成 N 条 Webhook_Delivery（每个匹配订阅一条）并入 Outbox 队列。
/// 收口原先散落在 ArchiveService / BackupService 各自内联的相同逻辑。
/// 语义约定（实现侧保证）：
///   · 受 Webhook 总开关 gated —— 关闭时不查订阅、不产生任何投递；
///   · 仅投递给「已启用 且 订阅了该事件名」的订阅；
///   · 自身任何异常仅吞咽记 Warning，绝不阻断触发它的主流程（事件发送是增益，非关键路径）。
/// HMAC 签名与重试退避在发送侧（WebhookOutboxWorker）处理，本契约只负责落 Pending 投递行 + 入队。
/// </remarks>
public interface IWebhookEmitter
{
    /// <summary>发射一个 Webhook 事件（事件名取 <see cref="Common.WebhookEvents"/> 常量，data 为事件负载对象）</summary>
    Task EmitAsync(string @event, object data, CancellationToken ct = default);
}
