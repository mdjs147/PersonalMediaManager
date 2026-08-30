using System.Net;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>代理解析契约：按消费方 + 目标 URL 决定是否走代理 / 返回代理地址</summary>
/// <remarks>
/// 配置来源：System_Setting 表的 5 个 Proxy_* key（详见 SystemSettingConfig 种子）。
/// AI 提供商不在此处的 UseFor 矩阵 — AI 是否走代理由每个 Parse_AiProvider.UseProxy 决定，
/// 调用 <see cref="TryGetProxy(ProxyConsumer, Uri, out Uri)"/> 时 consumer 传 <see cref="ProxyConsumer.Ai"/>
/// 仅代表"AI 总闸已被上层勾选"，最终是否走代理仍需结合 bypass 命中判断。
///
/// 缓存：内部 60 秒 TTL；用户改完代理设置后最多 60 秒生效。
///
/// IWebProxy 适配：调用方应使用 <see cref="CreateWebProxy(ProxyConsumer)"/> 把 consumer 绑定到
/// HttpClient 的 PrimaryHandler.Proxy，handler 每次发请求都会回调当前 Resolver 重新判断。
/// </remarks>
public interface IProxyResolver
{
    /// <summary>
    /// 判断 consumer 访问 target 是否需要走代理；走则置 proxyUri 并返回 true，
    /// 不走（总开关关 / 该 consumer 未勾选 / 命中 bypass）返回 false。
    /// </summary>
    bool TryGetProxy(ProxyConsumer consumer, Uri target, out Uri? proxyUri);

    /// <summary>构造一个 IWebProxy，可直接挂到 SocketsHttpHandler.Proxy</summary>
    IWebProxy CreateWebProxy(ProxyConsumer consumer);

    /// <summary>立即失效内存缓存（设置变更后调用，可让下次请求立刻读到新配置）</summary>
    void Invalidate();
}

/// <summary>代理消费方枚举（决定是否启用 + 哪个开关控制）</summary>
public enum ProxyConsumer
{
    /// <summary>TMDB 客户端 / 连通性探测 / 海报下载（受 Proxy_UseForTmdb 控制）</summary>
    Tmdb,

    /// <summary>AI 提供商（受 Parse_AiProvider.UseProxy 控制，调用方决定是否传入该 consumer）</summary>
    Ai,

    /// <summary>GitHub 升级检查（受 Proxy_UseForUpdateCheck 控制）</summary>
    UpdateCheck,

    /// <summary>字幕源客户端（受字幕设置 UseProxy 控制，调用方决定是否选用 Proxy named client，同 Ai 模式）</summary>
    Subtitle,
}
