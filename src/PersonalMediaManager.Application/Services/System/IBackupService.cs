using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Services.System;

/// <summary>数据库自动备份服务 — VACUUM INTO 在线快照 + 密钥环打包 + 按份数保留清理</summary>
/// <remarks>
/// 实现位于 Infrastructure.Persistence/Services/Maintenance/BackupService.cs（VACUUM 用 SqliteConnection、读 System_Setting 需 DbContext）：
/// - 备份产物：AppPaths.BackupDir/pmm-backup-{yyyyMMddHHmmss}.zip，含 pmm.db（干净快照）+ keys/*（DataProtection 密钥环）。
///   密钥环必须一并打包——否则恢复到新数据根后 TMDB / AI / PAT 等加密字段解不开（数据根漂移坑）。
/// - 保留策略：按文件名时间戳保留最近 Backup_RetainCount 份，其余删最旧。
/// - 两个入口：手动「立即备份」忽略开关；定时 BackupJob 读 Backup_Enabled 决定是否执行。
/// </remarks>
public interface IBackupService
{
    /// <summary>立即执行一次备份 + 保留清理（手动触发，忽略 Backup_Enabled 开关）</summary>
    Task<BackupResult> CreateAsync(CancellationToken ct = default);

    /// <summary>定时执行：读 Backup_Enabled，启用才备份 + 清理（BackupJob 调用）；停用返回 Performed=false</summary>
    Task<BackupResult> RunScheduledAsync(CancellationToken ct = default);
}
