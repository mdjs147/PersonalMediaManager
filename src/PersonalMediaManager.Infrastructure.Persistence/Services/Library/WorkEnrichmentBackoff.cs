using System.Collections.Concurrent;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Library;

/// <summary>富化远端失败短期退避表 — 进程内单例</summary>
/// <remarks>
/// 惰性富化 / 分季拉取在远端失败（超时 / 网络异常 / TMDB 非 2xx）后登记一个短窗口；窗口内同键直接跳过远端、
/// 即时降级返回本地已有数据，不再每次点击都吃满 HttpClient 超时。窗口到期或一次成功后自动解除。
/// 键由调用方约定（如 work:tv:1429 / season:1429:2）。无外部状态，单例线程安全（ConcurrentDictionary）。
/// </remarks>
internal sealed class WorkEnrichmentBackoff
{
    /// <summary>失败退避窗口（远端不可达时的静默期）</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    /// <summary>该键是否处于退避窗口内（窗口内应跳过远端）</summary>
    public bool IsBackedOff(string key)
        => _until.TryGetValue(key, out DateTimeOffset until) && until > DateTimeOffset.UtcNow;

    /// <summary>登记一次远端失败，开启 Window 退避窗口</summary>
    public void MarkFailure(string key)
        => _until[key] = DateTimeOffset.UtcNow.Add(Window);

    /// <summary>一次成功后解除退避</summary>
    public void Clear(string key)
        => _until.TryRemove(key, out _);
}
