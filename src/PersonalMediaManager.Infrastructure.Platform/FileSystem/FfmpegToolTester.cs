using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>ffmpeg / ffprobe 可用性自检实现 / runs -version</summary>
/// <remarks>
/// 跑 <c>{exe} -version</c>（轻量、不碰媒体文件）验证可启动，stdout 首行即版本号。
/// 复用 <see cref="FfmpegProcessRunner"/>（ArgumentList 逐参 + 抽干管道 + 超时 Kill 进程树）。
/// 任何失败归 Runnable=false + Error 不抛（仅「真取消」上抛），与探测 / 重混的「缺工具不阻断」风格一致。
/// </remarks>
internal sealed class FfmpegToolTester : IFfmpegToolTester
{
    /// <summary>-version 秒级返回；15s 上限足够覆盖冷启动 / 慢盘，超时即判不可用</summary>
    private const int VersionTimeoutSeconds = 15;

    private readonly ILogger<FfmpegToolTester> _logger;

    public FfmpegToolTester(ILogger<FfmpegToolTester> logger) => _logger = logger;

    public async Task<FfmpegToolProbe> ProbeAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return new FfmpegToolProbe(false, null, $"可执行文件不存在：{exePath}");

        try
        {
            ProcessRunResult run = await FfmpegProcessRunner.RunAsync(
                exePath, new[] { "-version" }, VersionTimeoutSeconds, ct);
            if (!run.Success)
                return new FfmpegToolProbe(false, null, $"退出码 {run.ExitCode}：{Truncate(run.StdErr)}");
            return new FfmpegToolProbe(true, FirstLine(run.StdOut), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg 工具自检异常：{Exe}", exePath);
            return new FfmpegToolProbe(false, null, ex.Message);
        }
    }

    /// <summary>取 stdout 首行并 Trim（ffmpeg/ffprobe -version 首行形如 "ffmpeg version 6.1.1-..."）</summary>
    private static string? FirstLine(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        int nl = stdout.IndexOfAny(['\r', '\n']);
        string line = (nl >= 0 ? stdout[..nl] : stdout).Trim();
        return line.Length == 0 ? null : line;
    }

    private static string Truncate(string? s, int max = 300)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
