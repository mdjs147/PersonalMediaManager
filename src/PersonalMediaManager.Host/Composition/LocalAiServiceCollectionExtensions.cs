using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Infrastructure.External.LocalAi;
using PersonalMediaManager.Infrastructure.Persistence.Services.LocalAi;

namespace PersonalMediaManager.Host.Composition;

/// <summary>本地模型独立装配，不自动启动推理进程</summary>
public static class LocalAiServiceCollectionExtensions
{
    public static IServiceCollection AddLocalAiServices(this IServiceCollection services)
    {
        services.AddLocalAiSettings().AddLocalAiRuntime();
        services.AddHostedService<LocalAiLifetimeService>();
        return services;
    }

    private sealed class LocalAiLifetimeService(ILocalAiRuntimeManager runtime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await runtime.CancelDownloadAsync(CancellationToken.None);
            await runtime.StopAsync(CancellationToken.None);
        }
    }
}
