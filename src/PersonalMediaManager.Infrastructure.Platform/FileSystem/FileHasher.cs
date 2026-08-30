using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>IFileHasher 实现 — 采样 SHA256（头部 + 尾部 + 文件大小）</summary>
/// <remarks>
/// 阈值 SampleThresholdBytes（16MB）分流：
///   · 文件 ≤ 阈值 → 全量 SHA256（小文件整盘读代价可忽略，且头尾会重叠、采样无意义）
///   · 文件 &gt; 阈值 → IncrementalHash 依次喂入：头部 SampleBlockBytes(8MB) + 尾部 8MB + 文件大小(8 字节小端)
/// 输出 Convert.ToHexString(...).ToLowerInvariant()（64 字符），与项目既有 SHA256 口径一致（TmdbSearchService）。
/// 选采样而非全量：处理管线全局串行（TaskProcessorWorker Semaphore(1,1)），对几十 GB 文件整盘读会卡死整条队列。
/// 采样稳定命中「重复下载 / 复制」这类完全相同文件（头尾 + 大小三重相同即同一文件）；
/// 对「头尾相同但中段不同」的构造性碰撞不设防——内容去重是增益特性，命中即省一次重复归档，未命中退化为照常处理。
/// </remarks>
internal sealed class FileHasher : IFileHasher
{
    /// <summary>采样阈值：文件 ≤ 16MB 走全量哈希，否则头尾采样</summary>
    private const long SampleThresholdBytes = 16L * 1024 * 1024;

    /// <summary>大文件头 / 尾各取的采样块大小：8MB</summary>
    private const int SampleBlockBytes = 8 * 1024 * 1024;

    /// <summary>流读缓冲区：80KB，与 Stream.CopyToAsync 默认一致</summary>
    private const int BufferSize = 81920;

    private readonly ILogger<FileHasher> _logger;

    public FileHasher(ILogger<FileHasher> logger)
    {
        _logger = logger;
    }

    public async Task<string?> TryComputeAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            FileInfo fi = new(filePath);
            if (!fi.Exists) return null;
            long length = fi.Length;

            byte[] hash = length <= SampleThresholdBytes
                ? await ComputeFullAsync(filePath, ct)
                : await ComputeSampledAsync(filePath, length, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 去重是增益特性：哈希失败（占用 / 权限 / 文件消失）只记警告并放弃去重，不中断主处理
            _logger.LogWarning(ex, "计算文件采样哈希失败，跳过内容去重：{Path}", filePath);
            return null;
        }
    }

    /// <summary>小文件全量 SHA256</summary>
    private static async Task<byte[]> ComputeFullAsync(string filePath, CancellationToken ct)
    {
        await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using SHA256 sha = SHA256.Create();
        return await sha.ComputeHashAsync(fs, ct);
    }

    /// <summary>大文件采样：头部 8MB + 尾部 8MB + 文件大小（8 字节小端）喂入 IncrementalHash</summary>
    private static async Task<byte[]> ComputeSampledAsync(string filePath, long length, CancellationToken ct)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];

        await using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
        {
            // 头部 8MB（从 0 开始）
            await FeedBlockAsync(hasher, fs, buffer, SampleBlockBytes, ct);
            // 尾部 8MB（seek 到末尾前 8MB；阈值已保证 length > 16MB，头尾不重叠）
            fs.Seek(length - SampleBlockBytes, SeekOrigin.Begin);
            await FeedBlockAsync(hasher, fs, buffer, SampleBlockBytes, ct);
        }

        // 文件大小并入指纹（8 字节小端），规避「头尾相同、大小不同」误判
        Span<byte> lenBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lenBytes, length);
        hasher.AppendData(lenBytes);

        return hasher.GetHashAndReset();
    }

    /// <summary>从当前位置读满 count 字节（分缓冲块）喂入哈希；遇流尾提前止（短读容错）</summary>
    private static async Task FeedBlockAsync(IncrementalHash hasher, FileStream fs, byte[] buffer, int count, CancellationToken ct)
    {
        int remaining = count;
        while (remaining > 0)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct);
            if (read == 0) break; // 到流尾
            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }
}
