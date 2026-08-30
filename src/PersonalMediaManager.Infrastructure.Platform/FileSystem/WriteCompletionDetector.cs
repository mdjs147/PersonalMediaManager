using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>IWriteCompletionDetector 实现（D4.6）— .complete 哨兵 + 稳定性窗口</summary>
/// <remarks>
/// 轮询间隔：1s（写入活跃期 1s 粒度足够）
/// 哨兵后缀：.complete（与下载器约定，常见于 qBittorrent 完成脚本 + 自定义网盘客户端）
/// 稳定判定：每轮拿 FileInfo.Length；连续 N 轮（N = stableSeconds）大小不变 → 写完
/// 超时：从首次进入循环算起，超过 timeoutSeconds → 返回 false（调用方决定记日志 / 重排队）
/// 文件不存在：立即短路返回 false，不空轮询整个超时窗口（旧实现等满 300s 才返回，被删 / 移走文件的
/// 僵尸记录每次重排都白等 5 分钟、拖住整条串行处理队列）；「消失」与「写入超时」的语义区分由调用方
/// （ProcessFileService）在拿到 false 后用 IFileProbe 复核源文件存在性完成。
///
/// 为何 ITimeProvider 不注入：本类 await Task.Delay（实际 CPU 时间）；测试时传小的 stableSeconds/timeoutSeconds 即可。
/// </remarks>
internal sealed class WriteCompletionDetector : IWriteCompletionDetector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const string SentinelSuffix = ".complete";

    private readonly ILogger<WriteCompletionDetector> _logger;

    public WriteCompletionDetector(ILogger<WriteCompletionDetector> logger)
    {
        _logger = logger;
    }

    public async Task<bool> WaitUntilCompleteAsync(
        string filePath, int stableSeconds = 5, int timeoutSeconds = 300, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("文件路径不能为空", nameof(filePath));
        if (stableSeconds < 1) stableSeconds = 1;
        if (timeoutSeconds < stableSeconds) timeoutSeconds = stableSeconds;

        string sentinelPath = filePath + SentinelSuffix;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        long? lastLength = null;
        int stableCount = 0;

        while (!ct.IsCancellationRequested)
        {
            // 文件不存在（被删除 / 移走 / 重命名）：短路提前返回，绝不空轮询整个超时窗口。
            // 即便误判瞬时状态（如下载器删旧文件再改名落位），文件随后出现也会被下一轮全量扫描重新发现，闭环自愈。
            // 注意此判定先于哨兵：哨兵在而源文件不在同样属于「源已消失」，返回 true 只会让下游对空文件白跑一遍。
            FileInfo info = new(filePath);
            if (!info.Exists)
            {
                _logger.LogWarning("文件不存在（可能被删除 / 移走 / 重命名），写入完成检测短路返回：{File}", filePath);
                return false;
            }

            // 哨兵优先：下载器明确通知就立即返回
            if (File.Exists(sentinelPath))
            {
                _logger.LogDebug("写入完成（哨兵 {Sentinel}）", sentinelPath);
                return true;
            }

            long len = info.Length;
            if (lastLength == len)
            {
                stableCount++;
                if (stableCount >= stableSeconds)
                {
                    _logger.LogDebug("写入完成（稳定 {Stable}s @ {Length} bytes）", stableCount, len);
                    return true;
                }
            }
            else
            {
                lastLength = len;
                stableCount = 1;   // 本轮算第一次「看到这个大小」
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogWarning("写入完成检测超时 {Timeout}s：{File}", timeoutSeconds, filePath);
                return false;
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }
        return false;
    }
}
