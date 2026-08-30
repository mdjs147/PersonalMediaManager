using System.Diagnostics;
using System.Text;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>外部进程执行封装 / ffprobe & ffmpeg runner</summary>
/// <remarks>
/// 用 ArgumentList 逐参传递（自动转义，安全处理含空格 / 中文 / 特殊字符的媒体路径，绝不手工拼命令行）；
/// 进程启动后立即挂上异步读取 stdout/stderr，边跑边抽干管道缓冲，避免缓冲填满导致子进程写阻塞死锁；
/// 超时或 ct 取消则 Kill 整个进程树。本类不记日志、不判业务——只负责「启动→收集输出→回收」，
/// 退出码与输出交调用方裁决。
/// </remarks>
internal static class FfmpegProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // 无 ct 版异步读取：随进程退出 / 被 Kill 后管道关闭自然完成，不会因取消抛 OCE 留下未观察异常
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* 抽干管道收尾，忽略 */ }
            // 区分「调用方真取消」（应用关停）与「超时」：ct 已取消则透传 OCE，否则报超时
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"外部进程超时（{timeoutSeconds}s）：{Path.GetFileName(exePath)}");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return new ProcessRunResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已自行退出 / 无权限，忽略
        }
    }
}

/// <summary>进程执行结果（退出码 + 标准输出 + 标准错误）</summary>
internal sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>退出码为 0</summary>
    public bool Success => ExitCode == 0;
}
