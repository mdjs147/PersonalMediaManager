using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>版本号信息提供方：暴露 4 套版本号 + commit + buildTime + 数据库 target/applied 对比</summary>
/// <remarks>
/// 数据来源：
/// 1. 静态字段（Product/Backend/Frontend/Commit/BuildTime/DbTarget）— 反射 entry assembly 的 AssemblyMetadataAttribute 与 AssemblyInformationalVersion
/// 2. 动态字段（Db.Applied/NeedsMigration）— 查 __EFMigrationsHistory + 对照嵌入资源 version-map.json
///
/// 两端点对应：
/// - GET /system/version（匿名）→ GetStatic() 不查 db，登录前可用
/// - GET /system/info（Admin）   → GetFullAsync(ct) 包含 db 动态字段
/// </remarks>
public interface IVersionInfoProvider
{
    /// <summary>静态版本号（不查 db，登录前 / 匿名可用）</summary>
    StaticVersionInfo GetStatic();

    /// <summary>完整版本号信息（含 db.applied / needsMigration，需要查 __EFMigrationsHistory）</summary>
    Task<VersionInfoResponse> GetFullAsync(CancellationToken ct = default);
}
