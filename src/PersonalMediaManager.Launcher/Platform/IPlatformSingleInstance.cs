namespace PersonalMediaManager.Launcher.Platform;

/// <summary>单实例守护抽象（隔离 OS 专属 API，便于单测）</summary>
/// <remarks>
/// Windows 实现：命名 Mutex Local\PersonalMediaManager-{SHA8} + 命名管道 \\.\pipe\PersonalMediaManager-IPC-{SHA8}
///
/// 流程：
/// 1. Launcher 启动调用 TryAcquire()
///    · 返回 true → 当前进程是主实例，订阅 OnSecondInstance 回调（弹浏览器到 :7288）
///    · 返回 false → 已有主实例，通过 IPC 发 "OPEN" 消息后退出（参见 Send 重载，主实例的回调被触发）
/// 2. 主实例 Dispose 时释放 Mutex / PidFile / Socket
/// </remarks>
public interface IPlatformSingleInstance : IDisposable
{
    /// <summary>尝试获取单实例锁；true=本进程为主实例，false=已有主实例（需调 NotifyExistingInstance 后退出）</summary>
    bool TryAcquire();

    /// <summary>非主实例时调用：通过 IPC 通知已运行的主实例「打开 WebUI」</summary>
    void NotifyExistingInstance();

    /// <summary>主实例订阅二次启动回调（IPC 收到 OPEN 时触发；典型用法：Process.Start("http://localhost:7288")）</summary>
    event Action OnSecondInstance;
}
