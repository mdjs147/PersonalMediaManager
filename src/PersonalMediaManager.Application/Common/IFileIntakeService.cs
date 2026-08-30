namespace PersonalMediaManager.Application.Common;

/// <summary>新发现文件登记入口（落库 + 入队单一通道）</summary>
/// <remarks>
/// 解决「排队任务只在内存 Channel、数据库无行」导致的两个问题：
///   1. 「处理队列」页 / Dashboard 队列长度只能查到当前正在处理的 1 条（串行管线），看不到真实积压；
///   2. 进程重启内存 Channel 即丢，StartupRecoveryWorker 无行可恢复 → 排队积压永久丢失。
///
/// 语义：先按 SourcePath 幂等建一行 Detected（DB 有 UQ_Media_Item_SourcePath 兜底并发），再入主管线队列。
/// 三个新文件入队点统一走此通道：FileWatcherWorker（实时事件）/ ScanService（全量扫描 + 手动整理）。
/// 重投类入口（History.Rescan / 强制全扫 Failed / Reprocess 删后重投）库里已有行或会重建，不经此通道。
/// </remarks>
public interface IFileIntakeService
{
    /// <summary>登记并入队一个新发现文件；已存在同 SourcePath 记录则跳过</summary>
    /// <returns>true=新建 Detected 行并入队；false=已存在或并发已建，未入队</returns>
    Task<bool> AdmitAsync(string fullPath, long watchFolderId, PendingFileSource source, CancellationToken ct = default);
}
