using System.Threading.Channels;

namespace PersonalMediaManager.Application.Common;

/// <summary>Webhook 出站队列契约（D6.3 OutboxWorker 消费）</summary>
/// <remarks>
/// 入队载荷只放 Webhook_Delivery 主键：Worker 取出后自己从 DB 加载最新状态，
/// 避免内存载荷与 DB 行不一致（订阅可能被改 / Secret 可能轮换）。
/// 生产者：
///   1. ArchiveService（D7.4）写入 Delivery 行 + EnqueueAsync 触发首次发送
///   2. WebhookRetryJob（D5.4）兜底唤醒 NextRetryAt &lt;= now 的 Pending/Retrying 行
/// </remarks>
public interface IWebhookOutboxQueue
{
    ValueTask EnqueueAsync(long deliveryId, CancellationToken cancellationToken = default);
    ChannelReader<long> Reader { get; }
}

/// <summary>基于 System.Threading.Channels 的默认实现</summary>
public sealed class WebhookOutboxQueue : IWebhookOutboxQueue
{
    /// <summary>默认容量 4096：webhook 短链路且每订阅独立，足够吞短时事件风暴</summary>
    public const int DefaultCapacity = 4096;

    private readonly Channel<long> _channel;

    public WebhookOutboxQueue() : this(DefaultCapacity) { }

    public WebhookOutboxQueue(int capacity)
    {
        _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ValueTask EnqueueAsync(long deliveryId, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(deliveryId, cancellationToken);

    public ChannelReader<long> Reader => _channel.Reader;
}
