using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>操作审计（Audit_Operation）</summary>
/// <remarks>
/// 不继承 Entity 的 UpdatedAt 语义（审计记录只追加，不更新）；Entity 基类提供的 UpdatedAt 在 EF 配置中将默认值设为 Timestamp 同值。
/// UserId 走 SET NULL：用户被删后审计保留可读，Username 冗余冗余。
/// 典型 Action：Auth.Login / Auth.LoginFailed / Settings.Update / Account.Create / Scan.Trigger 等。
/// </remarks>
public sealed class AuditOperation : Entity
{
    public long? UserId { get; set; }

    /// <summary>冗余存储（用户被删后仍可读）</summary>
    public string? Username { get; set; }

    public string Action { get; set; } = default!;

    /// <summary>目标资源标识（如 WatchFolder:12）</summary>
    public string? Target { get; set; }

    /// <summary>JSON：变更前后或附加信息</summary>
    public string? Detail { get; set; }

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>发生时间（UTC）；审计记录 = CreatedAt = UpdatedAt = Timestamp，三列同值</summary>
    public DateTimeOffset Timestamp { get; set; }
}
