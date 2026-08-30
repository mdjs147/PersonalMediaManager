using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>用户账号（User_Account）</summary>
/// <remarks>
/// 密码 BCrypt（cost=10，输出 60 字符）；登录失败不锁定，仅写 Audit_Operation（需求文档 §3.12）。
/// 首次向导必须建至少 1 个 Admin；后续不能删除最后一个 Admin（B6.3 校验，→ 1000）。
/// </remarks>
public sealed class UserAccount : Entity
{
    /// <summary>登录名（全局唯一，大小写敏感）</summary>
    public string Username { get; set; } = default!;

    /// <summary>BCrypt 哈希（60 字符）</summary>
    public string PasswordHash { get; set; } = default!;

    /// <summary>角色</summary>
    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>最近一次登录时间（UTC）</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>最近一次登录 IP（IPv6 兼容，最长 45）</summary>
    public string? LastLoginIp { get; set; }
}
