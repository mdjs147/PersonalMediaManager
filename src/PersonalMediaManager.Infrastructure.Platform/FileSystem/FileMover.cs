using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>IFileMover 实现（D4.1）— 同卷 rename + 跨卷 Copy+Delete + .tmp 原子可见</summary>
/// <remarks>
/// 算法：
/// - MoveAsync：先尝试 File.Move（同卷 = 元数据操作，秒级）；IOException 32 (cross-device link) → 走 CopyTempThenDelete
/// - CopyAsync：永远 Copy（保留源）
/// - Copy 路径全部经过 {target}.{guid}.tmp 写入 → File.Move 到最终名：原子化避免 Plex 扫到半成品
/// - 跨卷 Move 用 CopyTo + Delete：CopyTo 完成 + 校验大小匹配 + Delete 源；任意一步失败回滚（删 .tmp / 留源）
/// - 同名冲突：File.Exists 一次性检测（race condition 在单进程串行处理下不存在，TaskProcessorWorker 全局 SemaphoreSlim(1,1)）
/// - 目标目录不存在则 Directory.CreateDirectory（递归）
///
/// CancellationToken：File.Copy 不支持 ct，但通过 FileStream + CopyToAsync 实现可中断（中断时删 .tmp）。
/// </remarks>
internal sealed class FileMover : IFileMover
{
    private const int BufferSize = 81920;   // 80KB，与 Stream.CopyToAsync 默认一致
    private const int HResultCrossDevice = unchecked((int)0x80070011); // ERROR_NOT_SAME_DEVICE

    private readonly ILogger<FileMover> _logger;

    public FileMover(ILogger<FileMover> logger)
    {
        _logger = logger;
    }

    public async Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        ValidateSource(sourcePath);
        EnsureNoConflict(targetPath);
        EnsureTargetDirectory(targetPath);

        try
        {
            File.Move(sourcePath, targetPath);
            _logger.LogInformation("文件移动（同卷 rename）：{Source} → {Target}", sourcePath, targetPath);
            return;
        }
        catch (IOException ex) when (IsCrossDevice(ex))
        {
            _logger.LogInformation("文件跨卷 Move，fallback 到 Copy+Delete：{Source} → {Target}", sourcePath, targetPath);
            await CopyTempThenRenameAsync(sourcePath, targetPath, ct);
            // Copy 成功后删源；若删源失败仅记日志不抛（目标已 OK）
            try
            {
                File.Delete(sourcePath);
            }
            catch (Exception delEx)
            {
                _logger.LogWarning(delEx, "跨卷 Move 后删除源文件失败：{Source}（目标已就绪）", sourcePath);
            }
        }
    }

    public async Task CopyAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        ValidateSource(sourcePath);
        EnsureNoConflict(targetPath);
        EnsureTargetDirectory(targetPath);

        await CopyTempThenRenameAsync(sourcePath, targetPath, ct);
        _logger.LogInformation("文件复制：{Source} → {Target}", sourcePath, targetPath);
    }

    /// <summary>原子复制：写到 {target}.{guid}.tmp，全部写完后 rename 到最终目标</summary>
    private async Task CopyTempThenRenameAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        // 自愈清扫：先清理历史残留的同目标陈旧 .tmp（上次复制失败后清理自身又失败 / 进程崩溃遗留的孤儿）。
        // 单进程串行处理（TaskProcessorWorker 全局 SemaphoreSlim(1,1)）保证此刻不存在同目标的在途 .tmp，可安全清扫。
        SweepStaleTempFiles(targetPath);

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream src = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (FileStream dst = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await src.CopyToAsync(dst, BufferSize, ct);
                await dst.FlushAsync(ct);
            }
            long srcLen = new FileInfo(sourcePath).Length;
            long dstLen = new FileInfo(tempPath).Length;
            if (srcLen != dstLen)
                throw new IOException($"复制后大小不匹配：src={srcLen} tmp={dstLen}");

            // rename 到最终路径（同盘下原子）
            File.Move(tempPath, targetPath);
        }
        catch
        {
            // 失败回滚：删 .tmp（如果存在）；不删源
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 吞 — 主异常更重要 */ }
            }
            throw;
        }
    }

    /// <summary>清扫目标同名模式的陈旧 .tmp 残留（{targetFileName}.*.tmp）</summary>
    /// <remarks>
    /// 跨卷 Copy 失败时的回滚删除自身可能失败（网络盘断开）、进程崩溃则根本来不及回滚 ——
    /// 两者都会留下永久孤儿 .tmp 占用磁盘。在下次复制同一目标前就地自愈，不引入独立清理任务。
    /// 任何清扫失败仅记日志不阻断本次复制（残留只占磁盘，不影响复制正确性——新 .tmp 名含新 Guid 必不冲突）。
    /// </remarks>
    private void SweepStaleTempFiles(string targetPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string pattern = Path.GetFileName(targetPath) + ".*.tmp";
            foreach (string stale in Directory.EnumerateFiles(dir, pattern))
            {
                // 双重校验扩展名：Win32 通配匹配存在 8.3 短名误中的历史怪癖，确保只删真正的 .tmp
                if (!stale.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Delete(stale);
                    _logger.LogInformation("清扫陈旧 .tmp 残留：{Path}", stale);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "陈旧 .tmp 残留清扫失败（跳过，不阻断复制）：{Path}", stale);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "陈旧 .tmp 残留枚举失败（跳过清扫，不阻断复制）：{Target}", targetPath);
        }
    }

    private static void ValidateSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("源路径不能为空", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("源文件不存在", sourcePath);
    }

    private static void EnsureNoConflict(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("目标路径不能为空", nameof(targetPath));
        if (File.Exists(targetPath))
            throw new FileConflictException(targetPath);
    }

    private static void EnsureTargetDirectory(string targetPath)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>判定 IOException 是否跨卷错误（Windows 17 = 不同设备 / Linux EXDEV = 18）</summary>
    private static bool IsCrossDevice(IOException ex)
    {
        // Windows: HResult = 0x80070011 (ERROR_NOT_SAME_DEVICE)
        if (ex.HResult == HResultCrossDevice) return true;
        // Linux: errno EXDEV = 18 → HResult = 0x80131620 in some runtimes，但更稳的是看消息（脆弱但 fallback OK）
        if (ex.Message.Contains("cross-device", StringComparison.OrdinalIgnoreCase)) return true;
        if (ex.Message.Contains("Invalid cross-device link", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
