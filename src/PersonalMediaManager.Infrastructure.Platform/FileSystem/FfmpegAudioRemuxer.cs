using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>音频重混实现 / ffmpeg stream-copy</summary>
/// <remarks>
/// 调外部 ffmpeg（<c>-map 0 -map -0:idx... -c copy</c>）流复制丢弃不兼容音轨，不重编码（无需 av3a 解码器）。
/// 直接输出到归档目标盘的临时文件再原子 rename，省一次大文件跨盘重写：
///   先写同目录 <c>.pmm-remux-{guid}{ext}</c>（保留目标扩展名，ffmpeg 据此选容器），成功后 File.Move 秒级 rename 到目标；
///   失败清理 .tmp、绝不留半成品、绝不动源。Move 语义重混成功后删源；Copy 语义保留源。
/// 失败抛异常（落地前失败语义：源未动，ProcessFileService 转 Failed 后可 Rescan）。
/// </remarks>
internal sealed class FfmpegAudioRemuxer : IAudioRemuxer
{
    /// <summary>重混超时（2 小时）：stream copy 速度≈盘吞吐，大文件给足上限防卡死，仍受 ct 取消即时中断</summary>
    private const int RemuxTimeoutSeconds = 7200;
    private readonly ILogger<FfmpegAudioRemuxer> _logger;

    public FfmpegAudioRemuxer(ILogger<FfmpegAudioRemuxer> logger) => _logger = logger;

    public async Task RemuxAsync(
        string ffmpegPath,
        string sourcePath,
        string targetPath,
        IReadOnlyList<int> dropStreamIndexes,
        bool deleteSourceAfter,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new FileNotFoundException($"ffmpeg 不存在：{ffmpegPath}", ffmpegPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("源文件不存在", sourcePath);
        if (File.Exists(targetPath))
            throw new FileConflictException(targetPath);
        if (dropStreamIndexes.Count == 0)
            throw new ArgumentException("重混丢轨列表为空", nameof(dropStreamIndexes));

        string targetDir = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("目标路径无目录部分", nameof(targetPath));
        Directory.CreateDirectory(targetDir);

        // 临时文件必须保留目标扩展名：ffmpeg 按输出扩展名选容器（muxer），用 .tmp 会「找不到输出格式」而失败。
        // 与目标同目录（同盘）→ 成功后 File.Move 是秒级原子 rename。
        string ext = Path.GetExtension(targetPath);
        string tmp = Path.Combine(targetDir, $".pmm-remux-{Guid.NewGuid():N}{ext}");

        List<string> args = new() { "-v", "error", "-y", "-i", sourcePath, "-map", "0" };
        foreach (int idx in dropStreamIndexes)
        {
            args.Add("-map");
            args.Add($"-0:{idx}"); // 排除指定全局流索引（不兼容音轨）
        }
        args.Add("-c");
        args.Add("copy");
        args.Add(tmp);

        try
        {
            ProcessRunResult run = await FfmpegProcessRunner.RunAsync(ffmpegPath, args, RemuxTimeoutSeconds, ct);
            if (!run.Success)
                throw new IOException($"ffmpeg 重混失败（退出码 {run.ExitCode}）：{Truncate(run.StdErr)}");
            if (!File.Exists(tmp))
                throw new IOException("ffmpeg 重混未产出输出文件");

            File.Move(tmp, targetPath); // 同盘原子 rename
            _logger.LogInformation("音频重混完成（丢 {Count} 轨）：{Source} → {Target}",
                dropStreamIndexes.Count, sourcePath, targetPath);
        }
        catch
        {
            TryDelete(tmp); // 失败清理半成品，绝不留 .tmp、绝不动源
            throw;
        }

        if (deleteSourceAfter)
        {
            try
            {
                File.Delete(sourcePath);
            }
            catch (Exception ex)
            {
                // 目标已就绪，删源失败不算 fatal（源残留交空目录清理 / 下次处理兜底），仅告警
                _logger.LogWarning(ex, "重混后删源失败（目标已就绪，源残留）：{Source}", sourcePath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败忽略（.tmp 残留无害，下次用新 Guid 不冲突）
        }
    }

    private static string Truncate(string? s, int max = 400)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
