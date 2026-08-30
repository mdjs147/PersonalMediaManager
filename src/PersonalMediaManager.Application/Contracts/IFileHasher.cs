namespace PersonalMediaManager.Application.Contracts;

/// <summary>文件内容采样哈希契约（内容去重）— 计算判定「同一内容」的指纹</summary>
/// <remarks>
/// 实现位于 Infrastructure.Platform/FileSystem/FileHasher.cs：
/// - 采样 SHA256：小文件（≤ 阈值）全量哈希；大文件取「头部 + 尾部 + 文件大小」三段喂入 SHA256，
///   避免对几十 GB 媒体文件整盘读一遍——处理管线全局串行（TaskProcessorWorker Semaphore(1,1)），
///   全量哈希会阻塞整条队列且无字节级进度反馈。
/// - 确定性：同一文件每次得到相同十六进制串（64 字符小写）；文件大小并入指纹，规避「头尾相同、大小不同」误判。
/// - 容错：IO 失败（文件消失 / 占用 / 权限）返回 null，由调用方降级——内容去重是增益特性，不应因哈希失败中断主处理。
/// </remarks>
public interface IFileHasher
{
    /// <summary>计算文件内容采样哈希；成功返回 64 字符十六进制小写串，IO 失败返回 null</summary>
    Task<string?> TryComputeAsync(string filePath, CancellationToken ct = default);
}
