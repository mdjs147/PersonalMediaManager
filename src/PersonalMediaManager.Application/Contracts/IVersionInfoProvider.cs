using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>提供统一产品版本、构建信息和数据库迁移状态</summary>
/// <remarks>
/// 数据来源：
/// 1. 主版本来自 ProductVersion 元数据；Backend/Frontend 是同值兼容字段，commit 与构建时间单独提供。
/// 2. 迁移目标来自 EF GetMigrations；动态状态比较完整 __EFMigrationsHistory 与代码迁移集合。
///
/// GetStatic 用于启动日志与匿名 GET /system/version，不访问数据库。
/// GetFullAsync 由管理员 GET /system/info 返回完整迁移诊断。
/// 数据库状态只读，不执行迁移；历史读取失败时明确返回 unknown。
/// </remarks>
public interface IVersionInfoProvider
{
    /// <summary>静态版本号（不查 db，登录前 / 匿名可用）</summary>
    StaticVersionInfo GetStatic();

    /// <summary>完整版本号信息（含 db.applied / needsMigration，需要查 __EFMigrationsHistory）</summary>
    Task<VersionInfoResponse> GetFullAsync(CancellationToken ct = default);
}
