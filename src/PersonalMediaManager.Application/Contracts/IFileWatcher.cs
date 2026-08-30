namespace PersonalMediaManager.Application.Contracts;

/// <summary>文件监控抽象（D4.7）— FileSystemWatcher 的可注入包装</summary>
/// <remarks>
/// 设计目的：让 FileWatcherWorker（D6.1）依赖接口而非 BCL 类型，便于：
/// 1. 单元测试用 Stub 实现注入合成事件，跳过真实 IO
/// 2. 多 watcher 实例共用启停模板（每个监控目录一个 watcher）
///
/// 事件订阅：通过 OnFileEvent 回调；启动时 Start，停止时 Dispose。
/// </remarks>
public interface IFileWatcher : IDisposable
{
    /// <summary>启动监控（订阅前必调）</summary>
    void Start();

    /// <summary>停止监控（Dispose 也会自动 stop）</summary>
    void Stop();

    /// <summary>文件事件回调（Created / Changed / Renamed / Deleted）</summary>
    event Action<FileWatcherEvent>? OnFileEvent;
}

/// <summary>文件系统事件</summary>
/// <param name="ChangeType">类型</param>
/// <param name="FullPath">绝对路径</param>
/// <param name="OldFullPath">Renamed 时的原路径，其他类型为 null</param>
public sealed record FileWatcherEvent(
    FileWatcherChangeType ChangeType,
    string FullPath,
    string? OldFullPath);

public enum FileWatcherChangeType
{
    Created,
    Changed,
    Renamed,
    Deleted,
}
