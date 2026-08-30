using System.ComponentModel.DataAnnotations;

namespace PersonalMediaManager.Application.Dtos.System;

/// <summary>系统信息（GET /system/info）</summary>
public sealed record SystemInfoResponse
{
    /// <summary>主版本号（对外展示值，等同 VersionInfo.Product）；向后兼容前端老字段</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>完整版本号信息：4 套版本号 + commit + buildTime + 数据库 target/applied 对比</summary>
    public VersionInfoResponse VersionInfo { get; init; } = new();

    public string Os { get; init; } = string.Empty;
    public string OsVersion { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Uptime { get; init; }
    public string DataRoot { get; init; } = string.Empty;
    public long DbSizeBytes { get; init; }
    public long LogDirSizeBytes { get; init; }
    public long PostersCacheSizeBytes { get; init; }

    /// <summary>最近一次成功备份时间（backups/ 内最新 pmm-backup-*.zip 的时间戳，UTC）；从无备份为 null，前端据此「久未备份」飘红</summary>
    public DateTimeOffset? LastSuccessfulBackupAt { get; init; }
}

/// <summary>导出响应：流由 Controller 直接返 FileResult，本 DTO 仅用于元数据日志</summary>
public sealed record ExportResult
{
    public string FileName { get; init; } = string.Empty;
    public long Size { get; init; }
}

/// <summary>备份执行结果（POST /system/backup 与 BackupJob 共用）</summary>
/// <param name="Performed">是否真的产生了备份（定时任务在 Backup_Enabled=false 时为 false；手动触发恒 true）</param>
/// <param name="FileName">备份 zip 文件名（Performed=false 时为 null）</param>
/// <param name="SizeBytes">备份 zip 字节大小</param>
/// <param name="PrunedCount">本次按保留份数清理掉的旧备份份数</param>
public sealed record BackupResult(bool Performed, string? FileName, long SizeBytes, int PrunedCount);

/// <summary>备份文件元数据（GET /system/backups 列表项）</summary>
/// <param name="FileName">备份 zip 文件名（pmm-backup-yyyyMMddHHmmss.zip）</param>
/// <param name="SizeBytes">文件字节大小</param>
/// <param name="CreatedAt">备份创建时间（自文件名时间戳解析，UTC；解析失败回退文件最后写入时间）</param>
public sealed record BackupFileInfo(string FileName, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>从备份恢复请求（POST /system/restore）</summary>
/// <remarks>恢复复用「导入暂存 + 启动期换库」：把所选备份的 pmm.db 写到 .import-pending + 合并密钥环，下次启动原子换库。</remarks>
public sealed record RestoreBackupRequest
{
    /// <summary>backups/ 目录下的备份文件名（pmm-backup-*.zip）；服务端严格校验：不含路径分隔符 / 相对段，必须匹配备份命名，防路径穿越</summary>
    [Required(ErrorMessage = "备份文件名不能为空")]
    public string FileName { get; init; } = string.Empty;
}

/// <summary>导入结果（POST /system/import）</summary>
/// <remarks>
/// 导入采用「暂存 + 启动期换库」：本接口仅把新库写到 &lt;db&gt;.import-pending，不在运行中热替换主库
/// （WAL 模式下热替换会被旧 WAL 回写覆盖，表现为导入不生效）。真正换库发生在下次启动、开库之前，
/// 届时自动把现库备份为 pmm.db.preimport-*。故 <see cref="RequiresRestart"/> 恒为 true，且必须完整退出重启进程。
/// </remarks>
public sealed record ImportResult
{
    /// <summary>暂存是否成功（成功后等待重启换库）</summary>
    public bool Success { get; init; }
    /// <summary>导入包已暂存的物理路径（下次启动时原子换库）</summary>
    public string? StagedPath { get; init; }
    /// <summary>必须重启应用以完成换库（恒为 true，前端据此弹窗引导用户退出重启）</summary>
    public bool RequiresRestart { get; init; }
    /// <summary>面向用户的中文提示（说明需重启生效 + 旧库将备份为 preimport）</summary>
    public string? Message { get; init; }
}

/// <summary>清空处理历史结果（POST /system/clear-history）</summary>
/// <param name="MediaItems">删除的 Media_Item 行数</param>
/// <param name="ProcessSteps">删除的 Process_Step 行数</param>
/// <param name="AiCalls">删除的 Audit_AiCall 行数</param>
/// <param name="Operations">删除的 Audit_Operation 行数</param>
/// <param name="ScheduledTaskRuns">删除的 Audit_ScheduledTaskRun 行数</param>
/// <param name="WebhookDeliveries">删除的 Webhook_Delivery 行数</param>
public sealed record ClearHistoryResult(
    int MediaItems,
    int ProcessSteps,
    int AiCalls,
    int Operations,
    int ScheduledTaskRuns,
    int WebhookDeliveries);

/// <summary>重置所有配置结果（POST /system/reset-config）</summary>
/// <param name="SystemSettings">删除的 System_Setting 行数</param>
/// <param name="WatchFolders">删除的 Watch_Folder 行数</param>
/// <param name="WatchIgnoreRules">恒 0：Watch_IgnoreRule 为出厂 HasData 基线，重置不删除（保留下载器临时文件忽略规则）</param>
/// <param name="ParseRules">删除的 Parse_Rule 行数</param>
/// <param name="ParseAiProviders">删除的 Parse_AiProvider 行数</param>
/// <param name="TmdbSettings">删除的 Tmdb_Setting 行数</param>
/// <param name="Categories">删除的 Category_Definition 行数</param>
/// <param name="CategoryMatchRules">删除的 Category_MatchRule 行数</param>
/// <param name="WebhookSubscriptions">删除的 Webhook_Subscription 行数</param>
/// <param name="ReseededDefaults">是否已重新种子默认数据（默认分类 + 默认解析规则）</param>
public sealed record ResetConfigResult(
    int SystemSettings,
    int WatchFolders,
    int WatchIgnoreRules,
    int ParseRules,
    int ParseAiProviders,
    int TmdbSettings,
    int Categories,
    int CategoryMatchRules,
    int WebhookSubscriptions,
    bool ReseededDefaults);
