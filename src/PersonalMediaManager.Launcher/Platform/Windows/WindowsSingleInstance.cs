#if PMM_WINDOWS
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PersonalMediaManager.Launcher.Platform.Windows;

/// <summary>Windows 单实例守护（命名 Mutex + 命名管道 IPC）</summary>
/// <remarks>
/// 设计要点：
/// - Mutex 名 Local\PersonalMediaManager-{SHA8(InstallPath)}
///   用 Local\（会话级命名空间）而非 Global\：进程已改 asInvoker 普通权限运行，而创建 Global\ 内核对象需
///   SeCreateGlobalPrivilege —— 普通令牌（含管理员账户的 UAC 过滤令牌）不持有该特权，Global\ 会抛
///   UnauthorizedAccessException。托盘程序恒在交互会话运行，Local\「单会话单实例」语义正合适。
///   绑定安装目录哈希，让同机不同版本 / 不同安装位置可共存（夜测版 + 稳定版 + 旧版本回滚目录互不干扰）。
/// - 命名管道 \\.\pipe\PersonalMediaManager-IPC-{SHA8} 同步带哈希，避免二启动「跨安装目录唤醒」
/// - 主实例 TryAcquire() 成功后启异步监听线程（while(!disposed) WaitForConnectionAsync + ReadLineAsync）
/// - Dispose 顺序：CancelToken → 关闭 PipeServer → 释放 Mutex（防止重启时句柄残留）
/// </remarks>
public sealed class WindowsSingleInstance : IPlatformSingleInstance
{
    private const string OpenCommand = "OPEN";

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private bool _disposed;

    public event Action? OnSecondInstance;

    /// <summary>生产构造：Mutex/管道名带安装目录 SHA8 哈希（同机多版本共存）</summary>
    public WindowsSingleInstance() : this(BuildDefaultMutexName(), BuildDefaultPipeName()) { }

    /// <summary>测试构造：可注入唯一 Mutex / 管道名实现测试隔离</summary>
    public WindowsSingleInstance(string mutexName, string pipeName)
    {
        _mutexName = mutexName;
        _pipeName = pipeName;
    }

    private static string InstallPathHash8()
    {
        // 取 AppContext.BaseDirectory（生产 = 安装目录；自包含发布是 exe 所在目录）
        // 大小写不敏感（Windows 路径）+ 去尾部分隔符
        string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(installRoot.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4); // 8 hex 字符
    }

    private static string BuildDefaultMutexName() => $@"Local\PersonalMediaManager-{InstallPathHash8()}";

    private static string BuildDefaultPipeName() => $"PersonalMediaManager-IPC-{InstallPathHash8()}";

    public bool TryAcquire()
    {
        ThrowIfDisposed();
        _mutex = new Mutex(initiallyOwned: false, _mutexName, out _);
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一个主实例崩溃未释放 Mutex；视为「已被抛弃 → 当前进程接管」
            _ownsMutex = true;
        }

        if (_ownsMutex)
        {
            _listenCts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_listenCts.Token));
        }
        return _ownsMutex;
    }

    public void NotifyExistingInstance()
    {
        ThrowIfDisposed();
        try
        {
            using NamedPipeClientStream client = new(".", _pipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            byte[] buf = Encoding.UTF8.GetBytes(OpenCommand + "\n");
            client.Write(buf, 0, buf.Length);
            client.Flush();
        }
        catch
        {
            // 主实例可能正在退出 / 管道未就绪：吞掉异常，由调用方按预期退出
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using StreamReader reader = new(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(ct);
                if (line?.Trim() == OpenCommand)
                {
                    try { OnSecondInstance?.Invoke(); }
                    catch { /* 用户回调异常不应中断监听循环 */ }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 单条管道异常（客户端断开 / IO 错）忽略，继续监听下一条
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _listenCts?.Cancel(); } catch { }
        try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _listenCts?.Dispose();

        if (_mutex is not null)
        {
            try
            {
                if (_ownsMutex) _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
#endif
