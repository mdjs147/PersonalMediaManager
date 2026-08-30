using System.Threading.Channels;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>监控重建信号总线契约（进程内，多生产单消费）</summary>
/// <remarks>
/// 解决「FileWatcherWorker 仅启动时读一次目录集」导致的两类运行时失效：
///   1. 网络共享断开后 FSW 内部失效，恢复可达也不再产生事件（监控静默死亡）；
///   2. 监控目录增/删/改/启停后 watcher 集合不随之增量调整（新增不监控、禁用仍采集）。
///
/// 生产者（多写）：
///   - NetworkShareMonitorWorker：可达性翻转 → ShareRecovered（重建 + 补扫）/ ShareLost（暂停监控）；
///   - WatchFolderService：目录 CRUD 落库后 → FolderCreated / FolderUpdated / FolderDeleted；
///   - WatcherAdapter：FSW Error 事件（缓冲溢出 / 句柄失效）→ WatcherFaulted（按 Path 定位待重建目录）。
/// 消费者（单读）：FileWatcherWorker 信号循环，按 Kind 增量挂载 / 卸载 / 重建对应目录的 watcher。
///
/// 实现为无界 Channel 单例（见 Application/Common/WatchRebuildSignal）：信号量级极小
/// （人工 CRUD + 60s 周期可达性翻转沿 + 偶发 FSW 故障），无洪峰风险，Publish 永不阻塞。
/// </remarks>
public interface IWatchRebuildSignal
{
    /// <summary>发布一个监控重建信号（非阻塞，多生产者线程安全）</summary>
    void Publish(WatchRebuildItem item);

    /// <summary>暴露读端给 FileWatcherWorker 单消费循环</summary>
    ChannelReader<WatchRebuildItem> Reader { get; }
}

/// <summary>监控重建信号项</summary>
/// <param name="Kind">变更类型（决定消费侧动作：挂载 / 卸载 / 重建 / 重建后补扫）</param>
/// <param name="FolderId">目标 WatchFolder 主键；WatcherFaulted 来自 FSW 回调拿不到 Id 时为 0，由 Path 定位</param>
/// <param name="Path">目录路径（WatcherFaulted 用于按已注册 watcher 反查 FolderId；其余类型仅日志参考）</param>
public sealed record WatchRebuildItem(WatchChangeKind Kind, long FolderId, string? Path = null);

/// <summary>监控目录变更类型</summary>
public enum WatchChangeKind
{
    /// <summary>新增监控目录（启用则挂载 watcher）</summary>
    FolderCreated = 1,

    /// <summary>修改监控目录（路径 / 启停等变化：按 DB 当前状态重建或卸载）</summary>
    FolderUpdated = 2,

    /// <summary>删除监控目录（卸载 watcher）</summary>
    FolderDeleted = 3,

    /// <summary>网络共享由可达转不可达（暂停该目录监控，避免死 FSW 挂着）</summary>
    ShareLost = 4,

    /// <summary>网络共享恢复可达（重建 watcher + 触发该目录一次全量补扫）</summary>
    ShareRecovered = 5,

    /// <summary>FSW 内部错误（缓冲溢出 / 监控失效，重建该目录 watcher + 补扫兜底丢失事件）</summary>
    WatcherFaulted = 6,
}
