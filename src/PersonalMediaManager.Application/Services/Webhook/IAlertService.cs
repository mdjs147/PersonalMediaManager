namespace PersonalMediaManager.Application.Services.Webhook;

/// <summary>系统告警服务（带抑制窗口的 Webhook 事件发射）</summary>
/// <remarks>
/// 在 <see cref="IWebhookEmitter"/> 之上加「同 alertKey 在抑制窗口内只发一次」防风暴：
/// 一次磁盘满 / 网络盘掉线会让批量文件逐条触发同根因告警，抑制窗口（System_AlertSuppressMinutes）把它们收敛成一条。
/// 用于 disk.low / share.unreachable / ai.all_unavailable 这类「持续 / 批量」型告警。
/// 自身吞咽异常，绝不阻断触发它的主流程。
/// </remarks>
public interface IAlertService
{
    /// <summary>触发一次告警；alertKey 决定抑制粒度（如 "disk.low:D:\"），@event 取 <see cref="Common.WebhookEvents"/> 常量，data 为负载</summary>
    Task RaiseAsync(string alertKey, string @event, object data, CancellationToken ct = default);
}
