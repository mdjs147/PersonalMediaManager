namespace PersonalMediaManager.Application.Dtos.System;

/// <summary>静态版本号信息（不查数据库）</summary>
/// <remarks>启动日志等离线展示用；主版本来自程序集元数据，数据库目标来自编译进程序集的 EF 迁移</remarks>
public sealed record StaticVersionInfo
{
    /// <summary>主版本号（对外展示，如 "0.1.0"）</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>兼容字段，与 Product 相同；提交信息见 Commit 和 Dirty</summary>
    public string Backend { get; init; } = string.Empty;

    /// <summary>兼容字段，与 Product 相同</summary>
    public string Frontend { get; init; } = string.Empty;

    /// <summary>兼容字段：代码中最新 EF MigrationId，不是产品版本号</summary>
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

/// <summary>产品版本信息及数据库迁移诊断</summary>
public sealed record VersionInfoResponse
{
    public string Product { get; init; } = string.Empty;
    /// <summary>兼容字段，与 Product 相同</summary>
    public string Backend { get; init; } = string.Empty;
    /// <summary>兼容字段，与 Product 相同</summary>
    public string Frontend { get; init; } = string.Empty;
    public DbVersionStatus Database { get; init; } = new();
    public string Commit { get; init; } = string.Empty;
    public bool Dirty { get; init; }
    public DateTimeOffset? BuildTime { get; init; }
    public string Framework { get; init; } = string.Empty;
}

/// <summary>数据库 EF 迁移状态，不维护独立发布版本号</summary>
public sealed record DbVersionStatus
{
    /// <summary>兼容字段：最新 EF MigrationId；未查询或无迁移时为空</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>兼容字段：已应用的最大 MigrationId；无记录或不可读为 "unknown"</summary>
    public string Applied { get; init; } = string.Empty;

    /// <summary>__EFMigrationsHistory 中最大的 MigrationId（用于诊断）</summary>
    public string? AppliedMigrationId { get; init; }

    /// <summary>可读历史中是否缺少代码已知迁移；须结合 Status 判断兼容性</summary>
    public bool NeedsMigration { get; init; }

    /// <summary>是否成功读取迁移历史；历史表未创建也为 true，未查询为 false</summary>
    public bool HistoryAvailable { get; init; }

    /// <summary>数据库迁移诊断状态</summary>
    /// <remarks>
    /// upToDate 表示集合一致；pending 表示正常待升级；incompatible 表示未知迁移或链中间缺口；
    /// unknown 表示读取失败；notChecked 用于匿名版本端点，未读取也不公开迁移标识。
    /// </remarks>
    public string Status { get; init; } = "unknown";

    /// <summary>代码中存在但尚未记录为已应用的 EF MigrationId</summary>
    public IReadOnlyList<string> PendingMigrationIds { get; init; } = [];

    /// <summary>数据库中存在但当前代码不认识的 EF MigrationId</summary>
    public IReadOnlyList<string> UnknownMigrationIds { get; init; } = [];
}
