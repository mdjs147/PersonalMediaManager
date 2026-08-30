namespace PersonalMediaManager.Domain.Enums;

/// <summary>用户角色（需求文档 §3.12）</summary>
/// <remarks>
/// 数据库 User_Account.Role 列以字符串存储（"Admin" / "Viewer"），便于人工 SQL 查阅；EF 配置 HasConversion。
/// Admin：修改任意配置、管理用户、确认队列、触发扫描。
/// Viewer：仅看仪表盘 / 队列 / 历史 / 日志（只读）。
/// 匿名访问（无 JWT）等同于公开端点白名单（需求文档 §3.12 匿名清单）。
/// </remarks>
public enum UserRole
{
    /// <summary>仅查看（只读）</summary>
    Viewer = 1,

    /// <summary>管理员（全权限）</summary>
    Admin = 2,
}
