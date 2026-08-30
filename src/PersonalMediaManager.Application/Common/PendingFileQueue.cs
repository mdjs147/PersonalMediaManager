using System.Threading.Channels;

namespace PersonalMediaManager.Application.Common;

/// <summary>主处理管线入队项</summary>
/// <param name="FullPath">绝对路径（FileSystemWatcher 给出的原样）</param>
/// <param name="WatchFolderId">所属 WatchFolder 主键；为 0 表示手动扫描 / 全量扫描时来源未绑定</param>
/// <param name="Source">入队来源（便于日志追踪：watcher / full-scan / manual）</param>
public sealed record PendingFileItem(string FullPath, long WatchFolderId, PendingFileSource Source);

/// <summary>入队来源</summary>
public enum PendingFileSource
{
    /// <summary>FileSystemWatcher 实时事件（D6.1）</summary>
    Watcher = 1,
    /// <summary>FullScanJob 周期补扫（D5.2 → D7.6）</summary>
    FullScan = 2,
    /// <summary>用户在 UI 手动触发的单目录扫描（D7.6）</summary>
    Manual = 3,
}

/// <summary>主处理管线队列契约</summary>
/// <remarks>
/// 单生产多源（Watcher / FullScan / Manual）→ 单消费（TaskProcessorWorker，D6.2）。
/// 设计原则：
///   - **有界队列**：默认 1024 上限，背压策略 Wait（生产者会异步等待空位），避免下载器突袭把内存撑爆。
///   - **公平消费**：FIFO，TaskProcessorWorker 通过全局 Semaphore(1,1) 一次取一个文件处理。
///   - **入队前已落库**：新文件经 IFileIntakeService 先建 Detected 行（UQ_Media_Item_SourcePath 唯一索引兜底并发）再入队，
///     故「处理队列」页能看到完整排队积压、进程重启可由 StartupRecoveryWorker 从库恢复；
///     channel 内项仅是「待 TaskProcessor 取走」的内存指针，对应 MediaItem 行已先行存在。
///   - **去重前移**：IFileIntakeService 按 SourcePath 幂等（已存在即跳过）；消费者 ProcessFileService 仍按
///     SourcePath 兜底一次，让不经 IFileIntakeService 的路径（Reprocess 删后重投等）同样安全。
/// </remarks>
public interface IPendingFileQueue
{
    /// <summary>入队（满则异步等待空位；取消则抛 OperationCanceledException）</summary>
    ValueTask EnqueueAsync(PendingFileItem item, CancellationToken cancellationToken = default);

    /// <summary>暴露读端给 TaskProcessorWorker 用 ReadAllAsync 异步迭代</summary>
    ChannelReader<PendingFileItem> Reader { get; }
}

/// <summary>基于 System.Threading.Channels 的默认实现（BCL 自带，零依赖）</summary>
public sealed class PendingFileQueue : IPendingFileQueue
{
    /// <summary>默认容量：1024 → 单文件平均 5 秒处理，可缓冲约 1.5 小时下载峰值</summary>
    public const int DefaultCapacity = 1024;

    private readonly Channel<PendingFileItem> _channel;

    public PendingFileQueue() : this(DefaultCapacity) { }

    public PendingFileQueue(int capacity)
    {
        _channel = Channel.CreateBounded<PendingFileItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ValueTask EnqueueAsync(PendingFileItem item, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(item, cancellationToken);

    public ChannelReader<PendingFileItem> Reader => _channel.Reader;
}
