using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;
using PersonalMediaManager.Infrastructure.External.Ai.Protocols;
using PersonalMediaManager.Infrastructure.External.Subtitles;
using PersonalMediaManager.Infrastructure.External.Tmdb;
using PersonalMediaManager.Infrastructure.External.Update;
using PersonalMediaManager.Infrastructure.External.Webhook;

namespace PersonalMediaManager.Infrastructure.External.DependencyInjection;

/// <summary>Infrastructure.External 注入扩展</summary>
/// <remarks>
/// 已落地：AI 提供商连通性探测（C.5）+ TMDB 连通性探测（C.6）+ TMDB 客户端 / 候选排序 / 海报下载（D.2）
/// + AI 协议策略（5 个 IAiProtocol）+ AiProtocolParser（IAiParser 解析门面）+ 对话客户端 IAiChatClient（D.3）
/// + 软件升级检查 IUpdateChecker（D9.1）+ Webhook 连通性探测 IWebhookTester
/// + 字幕源策略（ISubtitleProvider：Assrt）+ SubtitleProviderRegistry（按枚举键路由门面）。
/// 必须依赖 IHttpClientFactory（调用方需先 services.AddHttpClient()）。
/// 每个协议策略注入独立 TokenBucketRateLimiter（1 req/s），故协议必须 Singleton 持有共享桶。
/// 5 个协议按 IEnumerable&lt;IAiProtocol&gt; 注入 → AiProtocolParser（IAiParser 门面）按 Protocol 路由。
///
/// 代理（Proxy 设置）：在此处集中给 TMDB 系 / AI 双轨 / GitHub UpdateCheck 命名 client 接入
/// IProxyResolver 创建的 PmmWebProxy；handler lifetime 默认 2 分钟，配置变更通过 IProxyResolver.Invalidate()
/// 即可让下次请求秒级生效（IWebProxy 每次回调）。
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>AI 走代理时使用的命名 HttpClient</summary>
    public const string AiHttpClientProxy = "AiProvider:Proxy";

    /// <summary>AI 直连时使用的命名 HttpClient</summary>
    public const string AiHttpClientDirect = "AiProvider:Direct";

    /// <summary>字幕源走代理时使用的命名 HttpClient</summary>
    public const string SubtitleHttpClientProxy = "SubtitleProxy";

    /// <summary>字幕源直连时使用的命名 HttpClient</summary>
    public const string SubtitleHttpClientDirect = "SubtitleDirect";

    public static IServiceCollection AddInfrastructureExternal(this IServiceCollection services)
    {
        services.AddSingleton<IAiProviderTester, AiProviderTester>();
        services.AddSingleton<ITmdbConnectivityTester, TmdbConnectivityTester>();
        services.AddSingleton<ITmdbClient, TmdbClient>();
        services.AddSingleton<IPosterDownloader, PosterDownloader>();
        services.AddSingleton<ITmdbImageProxy, TmdbImageProxy>();
        services.AddSingleton<IWebhookTester, WebhookTester>();
        // 软件升级检查（D9.1 / CLAUDE.md §9.5）：GitHub /releases/latest，含 ETag 缓存与错误归一化
        services.AddSingleton<IUpdateChecker, GitHubUpdateChecker>();

        // ── 命名 HttpClient + Proxy handler 装配 ──
        // TMDB 系（受 Proxy_UseForTmdb 控制）：3 个命名 client 共享同一个 PmmWebProxy(Tmdb)
        services.AddHttpClient("TmdbClient").ConfigurePrimaryHttpMessageHandler(MakeProxyHandlerFactory(ProxyConsumer.Tmdb));
        services.AddHttpClient("TmdbConnectivityTester").ConfigurePrimaryHttpMessageHandler(MakeProxyHandlerFactory(ProxyConsumer.Tmdb));
        services.AddHttpClient("TmdbPosterDownloader").ConfigurePrimaryHttpMessageHandler(MakeProxyHandlerFactory(ProxyConsumer.Tmdb));
        services.AddHttpClient(TmdbImageProxy.HttpClientName).ConfigurePrimaryHttpMessageHandler(MakeProxyHandlerFactory(ProxyConsumer.Tmdb));

        // AI 走 Direct / Proxy 两个共享 named client；各 Provider 内部按 endpoint.UseProxy 选 client name
        services.AddHttpClient(AiHttpClientDirect).ConfigurePrimaryHttpMessageHandler(MakeDirectHandlerFactory());
        services.AddHttpClient(AiHttpClientProxy).ConfigurePrimaryHttpMessageHandler(MakeProxyHandlerFactory(ProxyConsumer.Ai));

        // 字幕源同 AI 双轨：Direct / Proxy 两个共享 named client；AssrtSubtitleProvider 按 endpoint.UseProxy 二选一
        services.AddHttpClient(SubtitleHttpClientDirect).ConfigurePrimaryHttpMessageHandler(MakeDirectHandlerFactory());
        services.AddHttpClient(SubtitleHttpClientProxy).ConfigurePrimaryHttpMessageHandler(MakeProxyHandlerFactory(ProxyConsumer.Subtitle));

        // 5 个协议策略：每个独立 1 req/s 节流桶；按 IAiProtocol 注入。
        // AiProtocolParser（解析门面）/ AiChatClient（自由对话）/ AiProviderTester（连通性探测）均按 Protocol 路由。
        services.AddSingleton<IAiProtocol>(sp => new OllamaProtocol(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OllamaProtocol>>(),
            NewAiRateLimiter()));
        services.AddSingleton<IAiProtocol>(sp => new OpenAiCompatibleProtocol(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenAiCompatibleProtocol>>(),
            NewAiRateLimiter()));
        services.AddSingleton<IAiProtocol>(sp => new AnthropicProtocol(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AnthropicProtocol>>(),
            NewAiRateLimiter()));
        services.AddSingleton<IAiProtocol>(sp => new GeminiProtocol(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GeminiProtocol>>(),
            NewAiRateLimiter()));
        services.AddSingleton<IAiProtocol>(sp => new AzureOpenAiProtocol(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AzureOpenAiProtocol>>(),
            NewAiRateLimiter()));

        // 解析门面：聚合全部协议策略，供编排层（Application）按协议路由 + 拼解析提示词/反解（隔开 Application→External 反向依赖）
        services.AddSingleton<IAiParser, AiProtocolParser>();

        // 通用 AI 对话客户端（测试集 Triage / SuggestRule 等自由 prompt 场景）：复用 resolver 选 provider + AI 双轨 client
        // Scoped：依赖 Scoped 的 IAiProviderResolver（避免 Singleton 捕获 Scoped 的 captive dependency）
        services.AddScoped<IAiChatClient, AiChatClient>();

        // 字幕源策略：Assrt（伪·射手网）独立令牌桶 容量 1 / 3.2s 补给（源站上限 20 次/分钟，留余量），
        // 故策略必须 Singleton 持有共享桶；Registry 聚合全部 ISubtitleProvider 按 Provider 枚举键路由。
        services.AddSingleton<ISubtitleProvider>(sp => new AssrtSubtitleProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AssrtSubtitleProvider>>(),
            new TokenBucketRateLimiter(capacity: 1, refillInterval: TimeSpan.FromSeconds(3.2))));
        services.AddSingleton<ISubtitleProviderRegistry, SubtitleProviderRegistry>();

        return services;
    }

    /// <summary>构造走代理的 HttpMessageHandler 工厂（PmmWebProxy 内部按 consumer + 目标 URL 动态判断）</summary>
    /// <remarks>
    /// SocketsHttpHandler.UseProxy=true + Proxy=PmmWebProxy；Resolver 关闭代理或命中 bypass 时 IWebProxy.IsBypassed
    /// 会返回 true，handler 自动直连。比起 HttpClientHandler 不读 HTTP_PROXY 环境变量，避免"我关了代理但环境变量在污染"的迷之 bug。
    /// </remarks>
    private static Func<IServiceProvider, HttpMessageHandler> MakeProxyHandlerFactory(ProxyConsumer consumer) =>
        sp => new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = sp.GetRequiredService<IProxyResolver>().CreateWebProxy(consumer),
        };

    /// <summary>构造直连 HttpMessageHandler 工厂（AI Provider 中用户没勾选「走代理」时用）</summary>
    private static Func<IServiceProvider, HttpMessageHandler> MakeDirectHandlerFactory() =>
        _ => new SocketsHttpHandler { UseProxy = false };

    /// <summary>单 provider 1 req/s 节流；测试可直接 new 替换</summary>
    private static TokenBucketRateLimiter NewAiRateLimiter() =>
        new(capacity: 1, refillInterval: TimeSpan.FromSeconds(1));
}
