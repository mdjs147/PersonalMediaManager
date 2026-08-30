namespace PersonalMediaManager.Application.Services.Scan;

/// <summary>扫描服务（D7.6）— 手动触发全量 / 单目录扫描，把发现的文件入主处理 channel</summary>
/// <remarks>
/// 与 FullScanJob（D5.2）的关系：本服务实现 IFullScanCoordinator.RunAsync 让 Quartz Job 共用同一逻辑；
/// API 的 POST /scan/trigger 也调 TriggerAllAsync；POST /scan/folder/{id} 调 ScanFolderAsync。
/// 同一时刻只允许 1 个扫描在跑（避免重复入队 + 抢 DB / IO），并发触发抛 BusinessException「已有扫描在进行中」。
/// </remarks>
public interface IScanService
{
    /// <summary>触发全量扫描：枚举所有 Enabled=true 监控目录</summary>
    /// <param name="force">
    /// 强制全扫开关。
    /// false（默认，保持兼容）：已存在同 SourcePath 的 MediaItem 一律跳过。
    /// true：遇到已存在的 MediaItem 时——Failed 状态自动走 Rescan（Failed→Queued + ClearError + 入队），
    /// Queued/Processing/Succeeded/Archived 仍跳过（避免破坏状态机硬约束 §6.2 与已归档审计链）。
    /// </param>
    Task<ScanTriggerResult> TriggerAllAsync(bool force = false, CancellationToken ct = default);

    /// <summary>触发单目录扫描</summary>
    Task<ScanFolderResult> ScanFolderAsync(long folderId, CancellationToken ct = default);

    /// <summary>手动整理：处理任意文件或目录路径（不必是已配置的监控目录）</summary>
    /// <remarks>
    /// 文件 → 校验视频后缀 + 去重后入队；目录 → 健壮递归枚举视频文件逐个去重入队。
    /// 入队来源 Manual，WatchFolderId=0（不绑定监控目录；多层路径上下文按 ProcessFileService 退化为单层父目录）。
    /// 与全量 / 单目录扫描共享全局锁，并发触发抛「已有扫描在进行中」。
    /// </remarks>
    Task<ScanPathResult> ScanPathAsync(string path, CancellationToken ct = default);
}

/// <summary>全量扫描结果</summary>
/// <param name="ScanId">本次扫描标识（前端可关联 SignalR 推送）</param>
/// <param name="FolderCount">参与扫描的启用目录数</param>
/// <param name="FilesEnqueued">入队文件总数（新文件 + force 模式下重投的 Failed 记录）</param>
/// <param name="FailedRescanned">force 模式下自动重投的 Failed 记录数（force=false 时恒为 0）</param>
public sealed record ScanTriggerResult(string ScanId, int FolderCount, int FilesEnqueued, int FailedRescanned);

/// <summary>单目录扫描结果</summary>
public sealed record ScanFolderResult(string ScanId, long FolderId, string Path, int FilesEnqueued);

/// <summary>手动整理结果</summary>
/// <param name="ScanId">本次操作标识</param>
/// <param name="Path">规范化后的输入路径</param>
/// <param name="IsDirectory">输入是否为目录（否则为单文件）</param>
/// <param name="FilesEnqueued">新入队文件数</param>
/// <param name="Skipped">因已存在同路径记录而跳过的文件数</param>
public sealed record ScanPathResult(string ScanId, string Path, bool IsDirectory, int FilesEnqueued, int Skipped);
