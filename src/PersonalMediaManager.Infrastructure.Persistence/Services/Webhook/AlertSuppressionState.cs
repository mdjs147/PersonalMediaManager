namespace PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

/// <summary>告警抑制状态（进程内单例，线程安全）</summary>
/// <remarks>
/// 记录每个 alertKey 最近一次发出时刻；窗口内重复触发被抑制。
/// 进程重启清零（重启后同一告警重发一次可接受，且不增 DB 写）。
/// 多个产生侧（归档磁盘 / 网络盘巡检 / AI 编排）并发调用，故全程 lock 保护。
/// </remarks>
internal sealed class AlertSuppressionState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new(StringComparer.Ordinal);

    /// <summary>判断该 alertKey 此刻是否应发出（首次 或 距上次 ≥ 窗口）；返回 true 时同时登记本次发出时刻</summary>
    public bool ShouldFire(string alertKey, TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastFired.TryGetValue(alertKey, out DateTimeOffset last) && now - last < window)
                return false;
            _lastFired[alertKey] = now;
            return true;
        }
    }
}
