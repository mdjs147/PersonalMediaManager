using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Aggregates.WatchDirectories;

/// <summary>WatchFolder 可达性变化事件（D1.4）</summary>
/// <remarks>
/// 由 NetworkShareMonitorWorker（D6.4）周期检测后触发；
/// IsReachable=true 时 FileWatcherWorker（D6.1）应重新挂载 FileSystemWatcher；
/// IsReachable=false 时停止该目录的监控并写日志，避免大量异常刷屏。
/// </remarks>
public sealed class WatchFolderReachabilityChangedEvent : IDomainEvent
{
    public WatchFolderReachabilityChangedEvent(long watchFolderId, string path, bool isReachable, DateTimeOffset occurredAt)
    {
        WatchFolderId = watchFolderId;
        Path = path;
        IsReachable = isReachable;
        OccurredAt = occurredAt;
    }

    public long WatchFolderId { get; }

    public string Path { get; }

    /// <summary>true = 由不可达 → 可达；false = 由可达 → 不可达</summary>
    public bool IsReachable { get; }

    public DateTimeOffset OccurredAt { get; }
}
