namespace PersonalMediaManager.Application.Dtos.System;

/// <summary>静态版本号信息（不查数据库）</summary>
/// <remarks>登录前 / 匿名 /system/version 用；数据来源是 entry assembly 的 AssemblyMetadataAttribute</remarks>
public sealed record StaticVersionInfo
{
    /// <summary>主版本号（对外展示，如 "0.1.0"）</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>后端版本号（含 +commit[.dirty] 后缀，如 "0.1.0+a1b2c3d4" 或 "0.1.0+a1b2c3d4.dirty"）</summary>
    public string Backend { get; init; } = string.Empty;

    /// <summary>前端版本号（package.json 的 version 字段）</summary>
    public string Frontend { get; init; } = string.Empty;

    /// <summary>数据库目标 schema 版本号（PmmDbVersion）</summary>
    public string DbVersionTarget { get; init; } = string.Empty;

    /// <summary>短 commit sha（8 位 hex；无 git 时为空）</summary>
    public string Commit { get; init; } = string.Empty;

    /// <summary>构建时工作区是否有未提交改动</summary>
    public bool Dirty { get; init; }

    /// <summary>构建时间 UTC（无构建期注入时为 null）</summary>
    public DateTimeOffset? BuildTime { get; init; }

    /// <summary>运行时框架描述（如 ".NET 10.0.0"）</summary>
    public string Framework { get; init; } = string.Empty;
}

/// <summary>完整版本号信息（含数据库 applied 状态）</summary>
public sealed record VersionInfoResponse
{
    public string Product { get; init; } = string.Empty;
    public string Backend { get; init; } = string.Empty;
    public string Frontend { get; init; } = string.Empty;
    public DbVersionStatus Database { get; init; } = new();
    public string Commit { get; init; } = string.Empty;
    public bool Dirty { get; init; }
    public DateTimeOffset? BuildTime { get; init; }
    public string Framework { get; init; } = string.Empty;
}

/// <summary>数据库版本号状态（target vs applied + needsMigration）</summary>
public sealed record DbVersionStatus
{
    /// <summary>代码声明的目标 schema 版本号（PmmDbVersion）</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>当前数据库实际 schema 版本号（由 version-map.json 反查最大 MigrationId 得出；未知为 "unknown"）</summary>
    public string Applied { get; init; } = string.Empty;

    /// <summary>__EFMigrationsHistory 中最大的 MigrationId（用于诊断）</summary>
    public string? AppliedMigrationId { get; init; }

    /// <summary>是否需要迁移（applied 落后于 target）</summary>
    public bool NeedsMigration { get; init; }
}
