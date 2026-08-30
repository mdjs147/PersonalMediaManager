using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>WatcherAdapter 工厂实现（D4.7）— 注入到 D6.1 FileWatcherWorker</summary>
/// <remarks>
/// rebuildSignal 透传给每个 WatcherAdapter：FSW Error 事件经其上报 WatcherFaulted 待重建；
/// 可空兼容旧单测直构（null 时 WatcherAdapter 仅记日志不上报，行为同旧版）。
/// </remarks>
internal sealed class WatcherAdapterFactory : IFileWatcherFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IWatchRebuildSignal? _rebuildSignal;

    public WatcherAdapterFactory(ILoggerFactory loggerFactory, IWatchRebuildSignal? rebuildSignal = null)
    {
        _loggerFactory = loggerFactory;
        _rebuildSignal = rebuildSignal;
    }

    public IFileWatcher Create(string path) =>
        new WatcherAdapter(path, _loggerFactory.CreateLogger<WatcherAdapter>(), _rebuildSignal);
}
