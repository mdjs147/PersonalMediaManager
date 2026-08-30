using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Aggregates.WatchDirectories;

/// <summary>监控目录聚合根（Watch_Folder）</summary>
/// <remarks>
/// D1.4：MarkReachable() / MarkUnreachable() 在状态扭转时产 WatchFolderReachabilityChangedEvent；
/// 仅状态发生变化时触发事件（已可达再 MarkReachable 不重复发事件，避免无谓 SignalR 广播）。
/// IsNetworkShare=true 进 NetworkShareMonitor 60s 周期检测；本地盘视为永远可达，不参与本机制。
/// IsTransit=true 建议开启「空目录清理」（System_Setting.File.CleanEmptyDir）。
/// AutoScan=true（默认）走「自动监控」：实时 FileSystemWatcher + 周期 FullScanJob + 网络盘可达性探测；
/// AutoScan=false 关闭全部自动机制，目录仅在用户手动点击扫描时才处理（Enabled 仍是总开关，禁用则连手动也不行）。
/// </remarks>
public sealed class WatchFolder : AggregateRoot
{
    /// <summary>绝对路径（按输入原样存储，不做分隔符转换）</summary>
    public string Path { get; set; } = default!;

    /// <summary>显示名（用户备注）</summary>
    public string? Alias { get; set; }

    public bool IsTransit { get; set; }

    public bool IsNetworkShare { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>是否自动监控（实时监听 + 周期扫描）；关闭后仅手动扫描可处理本目录</summary>
    public bool AutoScan { get; set; } = true;

    /// <summary>扫描优先级，越小越先</summary>
    public int Priority { get; set; } = 100;

    /// <summary>最近一次全量扫描完成时间</summary>
    public DateTimeOffset? LastScanAt { get; set; }

    /// <summary>网络盘最近一次可达时间</summary>
    public DateTimeOffset? LastReachableAt { get; set; }

    /// <summary>D1.4：标记网络共享可达；若由不可达转为可达则发事件</summary>
    /// <remarks>
    /// 调用方：NetworkShareMonitorWorker（D6.4）周期检测后；
    /// 幂等：已经可达再调不发事件，仅刷新 LastReachableAt。
    /// 仅 IsNetworkShare=true 才有意义，本机本地盘可以不调用。
    /// </remarks>
    public void MarkReachable(DateTimeOffset now)
    {
        bool wasUnreachable = LastReachableAt is null
            || (LastReachableAt.HasValue && LastReachableAt.Value < now.AddMinutes(-2)); // 2 分钟内未刷新视为先前不可达
        LastReachableAt = now;
        if (wasUnreachable)
        {
            RaiseEvent(new WatchFolderReachabilityChangedEvent(Id, Path, isReachable: true, now));
        }
    }

    /// <summary>D1.4：标记网络共享不可达；若由可达转为不可达则发事件</summary>
    /// <remarks>
    /// 调用方：NetworkShareMonitorWorker 探测失败时；
    /// 实现：仅当 LastReachableAt 在「最近 2 分钟」内（视为之前可达）才发事件 + 清 LastReachableAt；
    /// 否则视为持续不可达，不重复发事件。
    /// </remarks>
    public void MarkUnreachable(DateTimeOffset now)
    {
        bool wasReachable = LastReachableAt.HasValue && LastReachableAt.Value >= now.AddMinutes(-2);
        if (wasReachable)
        {
            RaiseEvent(new WatchFolderReachabilityChangedEvent(Id, Path, isReachable: false, now));
        }
        LastReachableAt = null;
    }
}
