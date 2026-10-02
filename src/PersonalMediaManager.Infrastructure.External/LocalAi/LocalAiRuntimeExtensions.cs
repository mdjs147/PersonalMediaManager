using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.LocalAi;

/// <summary>本地模型受限网络与运行时注册</summary>
public static class LocalAiRuntimeExtensions
{
    public static IServiceCollection AddLocalAiRuntime(this IServiceCollection services)
    {
        services.AddHttpClient("LocalAi.Loopback", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false
            });
        services.AddHttpClient("LocalAi.Download", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false
            });
        services.AddSingleton<ILocalAiProcessFactory, LocalAiProcessFactory>();
        services.AddSingleton(sp => new LocalAiModelDownloader(sp.GetRequiredService<IHttpClientFactory>().CreateClient("LocalAi.Download"),
            Path.Combine(sp.GetRequiredService<AppPaths>().Root, "local-ai-models")));
        services.AddSingleton(sp => new LocalAiRuntimeManager(sp.GetRequiredService<ILocalAiSettingsService>(),
            sp.GetRequiredService<LocalAiModelDownloader>(), sp.GetRequiredService<ILocalAiProcessFactory>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("LocalAi.Loopback")));
        services.AddSingleton<ILocalAiRuntimeManager>(sp => sp.GetRequiredService<LocalAiRuntimeManager>());
        services.AddSingleton<ILocalAiInferenceClient>(sp => sp.GetRequiredService<LocalAiRuntimeManager>());
        return services;
    }
}
