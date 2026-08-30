using System.Collections.Concurrent;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform;

/// <summary>IAiProviderRpmGate 内存实现：按 providerId 维护滑动 60 秒请求时间戳队列</summary>
/// <remarks>
/// 每个 provider 一个时间戳队列 + 独立锁；IsThrottled / Record 都先剔除窗口外（早于 now-60s）的时间戳再判定 / 入队。
/// 用 <see cref="IClock"/> 取时间（可测）。ConcurrentDictionary 管 provider 分片，单 provider 内用 lock 串行化队列读写
/// （简单可靠；AI 调用并发度低，锁争用可忽略）。队列按时间递增入队，剔除只需从头 Peek/Dequeue。
/// </remarks>
internal sealed class AiProviderRpmGate : IAiProviderRpmGate
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<long, Queue<DateTimeOffset>> _hits = new();

    public AiProviderRpmGate(IClock clock) => _clock = clock;

    public bool IsThrottled(long providerId, int? rpmLimit)
    {
        if (rpmLimit is not > 0) return false; // null / ≤0 = 不限流
        Queue<DateTimeOffset> q = _hits.GetOrAdd(providerId, static _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            Trim(q, _clock.UtcNow);
            return q.Count >= rpmLimit.Value;
        }
    }

    public void Record(long providerId)
    {
        Queue<DateTimeOffset> q = _hits.GetOrAdd(providerId, static _ => new Queue<DateTimeOffset>());
        DateTimeOffset now = _clock.UtcNow;
        lock (q)
        {
            Trim(q, now);
            q.Enqueue(now);
        }
    }

    /// <summary>剔除滑出窗口（早于 now-60s）的时间戳（队列按时间递增，从头剔即可）</summary>
    private static void Trim(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - Window;
        while (q.Count > 0 && q.Peek() <= cutoff)
            q.Dequeue();
    }
}
