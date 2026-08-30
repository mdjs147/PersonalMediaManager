using System.Threading.Channels;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Common;

/// <summary>IWatchRebuildSignal 的无界 Channel 默认实现（BCL 自带，零依赖）</summary>
/// <remarks>
/// 与 PendingFileQueue 同款进程内 Channel 模式；选无界的本因：信号源全部是低频事件
/// （人工 CRUD / 60s 周期可达性翻转沿 / 偶发 FSW 故障），不存在洪峰；无界保证生产侧
/// Publish 永不阻塞调用线程（WatcherAdapter 的 FSW 回调线程 / WatchFolderService 的请求线程）。
/// SingleReader=true：唯一消费者是 FileWatcherWorker 的信号循环。
/// </remarks>
public sealed class WatchRebuildSignal : IWatchRebuildSignal
{
    private readonly Channel<WatchRebuildItem> _channel = Channel.CreateUnbounded<WatchRebuildItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Publish(WatchRebuildItem item) => _channel.Writer.TryWrite(item);

    public ChannelReader<WatchRebuildItem> Reader => _channel.Reader;
}
