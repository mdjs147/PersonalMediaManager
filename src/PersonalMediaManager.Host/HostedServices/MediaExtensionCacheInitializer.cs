using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>启动时预热媒体扩展名内存缓存</summary>
/// <remarks>
/// 注册为 IHostedService（BackgroundService）；在 StartupRecoveryWorker 之前运行，
/// 确保 FileWatcherWorker 启动时 IMediaExtensionProvider 已持有 DB 数据而非静态降级值。
/// </remarks>
internal sealed class MediaExtensionCacheInitializer : IHostedService
{
    private readonly IMediaExtensionProvider _provider;
    private readonly ILogger<MediaExtensionCacheInitializer> _logger;

    public MediaExtensionCacheInitializer(IMediaExtensionProvider provider, ILogger<MediaExtensionCacheInitializer> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _provider.RefreshAsync(cancellationToken);
            _logger.LogInformation("媒体扩展名缓存预热完成，共 {Count} 条启用扩展名",
                _provider.GetEnabledExtensions().Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "媒体扩展名缓存预热失败，降级使用内置白名单");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
