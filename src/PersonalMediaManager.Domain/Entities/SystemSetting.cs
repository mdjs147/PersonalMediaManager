using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>全局 KV 配置（System_Setting）</summary>
/// <remarks>
/// PK = Key（string），不继承 Entity；独立维护 CreatedAt / UpdatedAt 以接入 TimestampInterceptor。
/// 典型 Key：Web.Port / Parse.ConfidenceThreshold / File.Operation / Scan.IntervalHours，详见数据库设计 §1.1。
/// Value 统一字符串，类型由消费方解析（避免引入额外的 ValueKind 列）。
/// </remarks>
public sealed class SystemSetting : IHasTimestamps
{
    /// <summary>配置键（全局唯一，PK）</summary>
    public string Key { get; set; } = default!;

    /// <summary>配置值（统一字符串）</summary>
    public string? Value { get; set; }

    /// <summary>分组（General / Parse / Tmdb / Webhook / Log 等）</summary>
    public string Category { get; set; } = "General";

    /// <summary>说明文本</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
