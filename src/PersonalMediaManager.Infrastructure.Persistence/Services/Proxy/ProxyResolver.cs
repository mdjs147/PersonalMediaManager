using System.Net;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Proxy;

/// <summary>IProxyResolver 实现：读 System_Setting 5 个 Proxy_* key + 内存缓存 60s + bypass 判断</summary>
/// <remarks>
/// 设计要点：
/// - 单例：HttpClient handler 在两分钟 lifetime 内复用同一个 IWebProxy 实例，必须线程安全。
/// - 缓存：60 秒 TTL；同一 PmmDbContext 内一次性把 5 个 key + bypass list 读出来，避免每请求查库。
/// - 失效：GeneralSettingsService 写入后调 Invalidate()（手动失效）；不强依赖事件机制，超时也兜底刷新。
/// - bypass 内置规则不可关：loopback / 私有网段 / *.local（详见 IsBypassedInternal），防止把内网流量送到代理。
/// </remarks>
internal sealed class ProxyResolver : IProxyResolver
{
    /// <summary>5 个 Proxy_* key</summary>
    public const string KeyEnabled = "Proxy_Enabled";
    public const string KeyHttpUrl = "Proxy_HttpUrl";
    public const string KeyBypassList = "Proxy_BypassList";
    public const string KeyUseForTmdb = "Proxy_UseForTmdb";
    public const string KeyUseForUpdateCheck = "Proxy_UseForUpdateCheck";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly object _gate = new();
    private ProxySnapshot _snapshot = ProxySnapshot.Empty;
    private DateTimeOffset _snapshotExpiresAt = DateTimeOffset.MinValue;

    public ProxyResolver(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public bool TryGetProxy(ProxyConsumer consumer, Uri target, out Uri? proxyUri)
    {
        proxyUri = null;
        ProxySnapshot s = LoadSnapshot();

        if (!s.Enabled || s.ProxyUri is null) return false;

        // consumer 子开关（AI 已经在调用前由 Parse_AiProvider.UseProxy 把关，进入这里默认放行）
        bool consumerEnabled = consumer switch
        {
            ProxyConsumer.Tmdb => s.UseForTmdb,
            ProxyConsumer.UpdateCheck => s.UseForUpdateCheck,
            ProxyConsumer.Ai => true,
            // 字幕源是否走代理由字幕设置 UseProxy 在上游把关（选中 Proxy named client 才进到这里），与 Ai 同模式
            ProxyConsumer.Subtitle => true,
            _ => false,
        };
        if (!consumerEnabled) return false;

        if (IsBypassed(target, s)) return false;

        proxyUri = s.ProxyUri;
        return true;
    }

    public IWebProxy CreateWebProxy(ProxyConsumer consumer) => new PmmWebProxy(this, consumer);

    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshotExpiresAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>读取 / 刷新快照（线程安全，超时或手动失效后才查库）</summary>
    private ProxySnapshot LoadSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_snapshotExpiresAt > now) return _snapshot;
        }

        ProxySnapshot fresh = LoadFromDbAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            _snapshot = fresh;
            _snapshotExpiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);
            return _snapshot;
        }
    }

    private async Task<ProxySnapshot> LoadFromDbAsync()
    {
        try
        {
            await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync();
            string[] keys = [KeyEnabled, KeyHttpUrl, KeyBypassList, KeyUseForTmdb, KeyUseForUpdateCheck];
            Dictionary<string, string?> kv = await ctx.SystemSettings.AsNoTracking()
                .Where(s => keys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            bool enabled = ParseBool(kv.GetValueOrDefault(KeyEnabled), false);
            string? urlRaw = kv.GetValueOrDefault(KeyHttpUrl);
            Uri? proxyUri = null;
            if (enabled && !string.IsNullOrWhiteSpace(urlRaw)
                && Uri.TryCreate(urlRaw.Trim(), UriKind.Absolute, out Uri? parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                proxyUri = parsed;
            }

            string[] extraBypass = ParseBypassList(kv.GetValueOrDefault(KeyBypassList));
            bool useForTmdb = ParseBool(kv.GetValueOrDefault(KeyUseForTmdb), true);
            bool useForUpdateCheck = ParseBool(kv.GetValueOrDefault(KeyUseForUpdateCheck), true);

            return new ProxySnapshot(enabled && proxyUri is not null, proxyUri, extraBypass, useForTmdb, useForUpdateCheck);
        }
        catch
        {
            // 启动期 db 未就绪 / 表不存在 → 视为代理关闭，保证主流程不挂
            return ProxySnapshot.Empty;
        }
    }

    /// <summary>合并内置 bypass + 用户自定义 bypass 判断是否跳过代理</summary>
    private static bool IsBypassed(Uri target, ProxySnapshot s)
    {
        // 内置规则（不可关，详见 IProxyResolver 注释）
        if (IsBuiltinBypass(target)) return true;

        // 用户自定义 bypass：通配符匹配 host
        if (s.ExtraBypass.Length == 0) return false;
        string host = target.Host;
        foreach (string pattern in s.ExtraBypass)
        {
            if (MatchesWildcard(host, pattern)) return true;
        }
        return false;
    }

    /// <summary>内置不可关 bypass：loopback / 私有网段 / *.local</summary>
    private static bool IsBuiltinBypass(Uri target)
    {
        string host = target.Host;
        if (string.IsNullOrEmpty(host)) return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;

        if (!IPAddress.TryParse(host, out IPAddress? ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 (link-local)
            if (b[0] == 169 && b[1] == 254) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local fe80::/10 + unique-local fc00::/7
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
            if ((b[0] & 0xfe) == 0xfc) return true;
        }
        return false;
    }

    /// <summary>host 与单条通配符规则匹配（支持 * 任意段，规则不区分大小写）</summary>
    private static bool MatchesWildcard(string host, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string p = pattern.Trim();
        if (p.Length == 0) return false;
        if (!p.Contains('*'))
            return string.Equals(host, p, StringComparison.OrdinalIgnoreCase);
        // 转正则：转义其他字符 + * → .*
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(host, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool ParseBool(string? raw, bool fallback)
        => bool.TryParse(raw, out bool v) ? v : fallback;

    private static string[] ParseBypassList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>不可变快照（线程安全复用）</summary>
    private sealed record ProxySnapshot(bool Enabled, Uri? ProxyUri, string[] ExtraBypass, bool UseForTmdb, bool UseForUpdateCheck)
    {
        public static readonly ProxySnapshot Empty = new(false, null, Array.Empty<string>(), true, true);
    }
}

/// <summary>IWebProxy 适配：每次 GetProxy / IsBypassed 都回调 IProxyResolver，配置变更立刻生效</summary>
internal sealed class PmmWebProxy : IWebProxy
{
    private readonly IProxyResolver _resolver;
    private readonly ProxyConsumer _consumer;

    public PmmWebProxy(IProxyResolver resolver, ProxyConsumer consumer)
    {
        _resolver = resolver;
        _consumer = consumer;
    }

    /// <summary>HTTP 代理一般无需鉴权；如需账密由用户在 URL 里塞 http://user:pass@host:port</summary>
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination)
        => _resolver.TryGetProxy(_consumer, destination, out Uri? p) ? p : null;

    public bool IsBypassed(Uri host)
        => !_resolver.TryGetProxy(_consumer, host, out _);
}
