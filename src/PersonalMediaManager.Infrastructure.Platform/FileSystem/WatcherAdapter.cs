using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>FileSystemWatcher 包装（D4.7）</summary>
/// <remarks>
/// 配置：
/// - IncludeSubdirectories=true：递归监控（默认监控目录树）
/// - NotifyFilter：FileName + DirectoryName + Size + LastWrite（覆盖移入 / 重命名 / 写入 4 类）
/// - InternalBufferSize=64KB：单次事件突增（大量小文件解压）避免溢出（默认 8KB 不够）
/// - EnableRaisingEvents：Start() 时置 true；Stop() / Dispose 时置 false
/// - Filter=*.* 留给后续在 FileWatcherWorker 按扩展名白名单过滤（Watch_Folder 配置的允许扩展集）
///
/// 事件去抖不在本类：FileWatcherWorker 用 Channel + 去重 dictionary 处理「同文件 10 次 Changed」场景。
/// 异常吞咽：FileSystemWatcher 的 Error 事件（缓冲区溢出 / 监控失效）记 WARN，并经 IWatchRebuildSignal
/// 发 WatcherFaulted 信号（带本目录 Path）→ FileWatcherWorker 收到后重建该目录 watcher + 补扫兜底丢失事件；
/// rebuildSignal 未注入（单测直构）时仅记日志，行为与旧版一致。
/// </remarks>
internal sealed class WatcherAdapter : IFileWatcher
{
    private readonly FileSystemWatcher _watcher;
    private readonly ILogger _logger;
    private bool _disposed;

    public event Action<FileWatcherEvent>? OnFileEvent;

    public WatcherAdapter(string path, ILogger logger, IWatchRebuildSignal? rebuildSignal = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("监控路径不能为空", nameof(path));
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"监控路径不存在：{path}");

        _logger = logger;
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.Size
                         | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Raise(FileWatcherChangeType.Created, e.FullPath, null);
        _watcher.Changed += (_, e) => Raise(FileWatcherChangeType.Changed, e.FullPath, null);
        _watcher.Deleted += (_, e) => Raise(FileWatcherChangeType.Deleted, e.FullPath, null);
        _watcher.Renamed += (_, e) => Raise(FileWatcherChangeType.Renamed, e.FullPath, e.OldFullPath);
        _watcher.Error += (_, e) =>
        {
            // FSW 内部错误（缓冲区溢出 / UNC 断开句柄失效）后实例可能已静默失效：仅记日志不够，
            // 必须上报待重建——FolderId 在本层不可知，传 0 + Path 由 FileWatcherWorker 按已注册 watcher 反查
            _logger.LogWarning(e.GetException(), "FileSystemWatcher 内部错误，已上报待重建（路径={Path}）", path);
            rebuildSignal?.Publish(new WatchRebuildItem(WatchChangeKind.WatcherFaulted, FolderId: 0, Path: path));
        };
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WatcherAdapter));
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_disposed) return;
        _watcher.EnableRaisingEvents = false;
    }

    private void Raise(FileWatcherChangeType type, string fullPath, string? oldFullPath)
    {
        try
        {
            OnFileEvent?.Invoke(new FileWatcherEvent(type, fullPath, oldFullPath));
        }
        catch (Exception ex)
        {
            // 不让回调异常打挂 watcher（一个文件出问题不应停止整个目录的监控）
            _logger.LogError(ex, "文件事件回调异常 {Type} {Path}", type, fullPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher.EnableRaisingEvents = false; } catch { }
        _watcher.Dispose();
    }
}
