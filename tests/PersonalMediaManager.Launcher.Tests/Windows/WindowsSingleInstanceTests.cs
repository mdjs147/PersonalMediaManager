#if PMM_WINDOWS
using PersonalMediaManager.Launcher.Platform.Windows;

namespace PersonalMediaManager.Launcher.Tests.Windows;

/// <summary>Windows 单实例测试：Mutex 二次获取失败 + 命名管道 IPC OPEN 回调触发</summary>
public sealed class WindowsSingleInstanceTests
{
    /// <summary>每个测试用唯一 Mutex/Pipe 名隔离</summary>
    private static (string Mutex, string Pipe) UniqueNames()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        return ($@"Local\PmmTest-{suffix}", $"PmmTest-{suffix}");
    }

    /// <summary>Mutex 在同一线程可重入；必须把「第二实例」放到独立线程才能复现生产里两个进程的竞争</summary>
    private static bool TryAcquireOnOtherThread(string mutexName, string pipeName)
    {
        bool result = false;
        Thread t = new(() =>
        {
            using WindowsSingleInstance instance = new(mutexName, pipeName);
            result = instance.TryAcquire();
        });
        t.Start();
        t.Join();
        return result;
    }

    [Fact(DisplayName = "TryAcquire 主实例返回 true，独立线程的第二实例返回 false")]
    public void SecondInstance_TryAcquire_ReturnsFalse()
    {
        (string m, string p) = UniqueNames();
        using WindowsSingleInstance first = new(m, p);
        first.TryAcquire().Should().BeTrue("第一个实例应获取 Mutex 成功");

        TryAcquireOnOtherThread(m, p).Should().BeFalse("独立线程的第二实例应被 Mutex 阻断");
    }

    [Fact(DisplayName = "NotifyExistingInstance 触发主实例 OnSecondInstance 事件")]
    public async Task NotifyExistingInstance_TriggersOnSecondInstance()
    {
        (string m, string p) = UniqueNames();
        using WindowsSingleInstance primary = new(m, p);
        primary.TryAcquire().Should().BeTrue();

        TaskCompletionSource tcs = new();
        primary.OnSecondInstance += () => tcs.TrySetResult();

        // 用独立线程模拟另一进程：实例化 → NotifyExistingInstance（不调 TryAcquire 避免与主线程混淆）
        Thread t = new(() =>
        {
            using WindowsSingleInstance secondary = new(m, p);
            secondary.NotifyExistingInstance();
        });
        t.Start();
        t.Join(TimeSpan.FromSeconds(5));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Dispose 释放 Mutex 后，新实例可重新获取")]
    public void Dispose_ReleasesMutex_AllowingReacquire()
    {
        (string m, string p) = UniqueNames();
        WindowsSingleInstance first = new(m, p);
        first.TryAcquire().Should().BeTrue();
        first.Dispose();

        using WindowsSingleInstance reacquired = new(m, p);
        reacquired.TryAcquire().Should().BeTrue("Mutex 释放后应可重新获取");
    }
}
#endif
