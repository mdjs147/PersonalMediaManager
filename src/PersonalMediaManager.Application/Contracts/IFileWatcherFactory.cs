namespace PersonalMediaManager.Application.Contracts;

/// <summary>每监控目录一个 IFileWatcher 实例的工厂（D4.7 → D6.1 使用）</summary>
/// <remarks>
/// FileWatcherWorker 启动时遍历 Watch_Folder 表，为每个 Enabled=true 行调一次 Create(path) 拿独立 watcher。
/// 工厂注册 Singleton；watcher 实例由调用方持有 + Dispose（生命周期与对应 Watch_Folder 行一致）。
/// </remarks>
public interface IFileWatcherFactory
{
    IFileWatcher Create(string path);
}
