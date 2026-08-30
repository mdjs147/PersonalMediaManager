namespace PersonalMediaManager.Domain.Enums;

/// <summary>Webhook 投递状态（Webhook_Delivery.Status）</summary>
/// <remarks>
/// 数据库以字符串存储（"Pending" / "Retrying" / "Success" / "Failed"），EF 配置 HasConversion；与设计文档 §1.14 对齐。
/// Pending：初次入队未发送；Retrying：失败但未达重试上限；Success：HTTP 2xx；Failed：达上限 3 次仍失败。
/// </remarks>
public enum WebhookDeliveryStatus
{
    Pending = 1,
    Retrying = 2,
    Success = 3,
    Failed = 4,
}
