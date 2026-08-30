namespace PersonalMediaManager.Infrastructure.External.Tmdb;

/// <summary>极简令牌桶限流（用于 TMDB 客户端 40 token/s 节流）</summary>
/// <remarks>
/// 选型理由：Polly v8 RateLimiter 子包配置复杂，且与 v7 API 漂移；自实现 ~40 行覆盖单进程节流够用。
/// 实现：每 refillInterval 注入 capacity 个 token；ConsumeAsync 拿不到 token 时 await Task.Delay(到下次注入)。
/// 并发安全：lock 保护 _tokens 与 _lastRefillAt；await Task.Delay 在锁外。
/// </remarks>
internal sealed class TokenBucketRateLimiter
{
    private readonly int _capacity;
    private readonly TimeSpan _refillInterval;
    private readonly object _gate = new();
    private int _tokens;
    private DateTimeOffset _lastRefillAt;
    private readonly Func<DateTimeOffset> _now;

    public TokenBucketRateLimiter(int capacity, TimeSpan refillInterval, Func<DateTimeOffset>? now = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refillInterval));
        _capacity = capacity;
        _refillInterval = refillInterval;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _tokens = capacity;
        _lastRefillAt = _now();
    }

    /// <summary>消耗 1 个 token；不足则 await 到下次补充</summary>
    public async Task ConsumeAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan waitFor;
            lock (_gate)
            {
                RefillLocked();
                if (_tokens > 0)
                {
                    _tokens--;
                    return;
                }
                // 计算到下次补充还需多久
                DateTimeOffset nextRefill = _lastRefillAt + _refillInterval;
                waitFor = nextRefill - _now();
                if (waitFor < TimeSpan.Zero) waitFor = TimeSpan.Zero;
            }
            if (waitFor > TimeSpan.Zero)
                await Task.Delay(waitFor, ct);
        }
    }

    private void RefillLocked()
    {
        DateTimeOffset n = _now();
        if (n - _lastRefillAt >= _refillInterval)
        {
            _tokens = _capacity;
            _lastRefillAt = n;
        }
    }
}
