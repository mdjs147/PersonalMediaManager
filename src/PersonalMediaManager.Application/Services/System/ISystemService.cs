using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Services.System;

/// <summary>系统模块应用服务（E.2 导入/导出 + 系统信息）</summary>
/// <remarks>
/// 三方法：
/// - GetInfoAsync：版本 / 运行时 / OS / 数据目录大小 → 仪表盘 + Settings 「关于」页用
/// - ExportAsync：VACUUM INTO 临时副本 → 打包 pmm.db + appsettings.json + keys → zip 返写流
/// - ImportAsync：解压前严格防 Zip Slip → 备份当前 pmm.db → 替换 → 返回 RequiresRestart=true
///
/// 实现位于 Infrastructure.Platform（涉及文件系统 IO + SQLite VACUUM INTO + DataProtection 密钥环）。
/// </remarks>
public interface ISystemService
{
    Task<SystemInfoResponse> GetInfoAsync(CancellationToken ct = default);

    /// <summary>导出系统快照到流。调用方负责设置 HTTP Content-Disposition + Content-Type</summary>
    /// <param name="output">输出流（一般是 HttpResponse.Body）</param>
    Task<ExportResult> ExportAsync(Stream output, CancellationToken ct = default);

    /// <summary>从 zip 流导入。要求是 ExportAsync 产物或同构清单（pmm.db / appsettings.json / keys/）</summary>
    /// <param name="input">输入流（HttpRequest.Form.Files[0].OpenReadStream()）</param>
    Task<ImportResult> ImportAsync(Stream input, CancellationToken ct = default);

    /// <summary>列出 backups/ 目录下的自动 / 手动备份 zip（按时间倒序），供「备份与恢复」选择恢复点</summary>
    Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>从指定备份恢复：复用导入暂存（写 .import-pending + 合并密钥环），下次启动换库。fileName 必须是 backups/ 内 pmm-backup-*.zip，严格校验防路径穿越</summary>
    Task<ImportResult> RestoreBackupAsync(string fileName, CancellationToken ct = default);
}
