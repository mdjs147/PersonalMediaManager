using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Host.Hubs;

/// <summary>ITaskNotifier 的 SignalR 实现（D8.3）— 桥接 Application 业务到 /hubs/tasks 客户端</summary>
/// <remarks>
/// 失败容错：Hub 无客户端连接 / SendAsync 异常 → 仅 Warning，不抛（通知不应阻断业务流）。
/// SignalR JsonProtocol 在 PmmHost 已注册 JsonStringEnumConverter，所以 MediaItemStatus 在前端可见为字符串（"AwaitingReview" 等）。
/// </remarks>
internal sealed class SignalRTaskNotifier : ITaskNotifier
{
    public const string StatusChangedMethod = "taskStatusChanged";
    public const string QueueChangedMethod = "queueChanged";

    private readonly IHubContext<TaskHub> _hub;
    private readonly ILogger<SignalRTaskNotifier> _logger;

    public SignalRTaskNotifier(IHubContext<TaskHub> hub, ILogger<SignalRTaskNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task NotifyStatusChangedAsync(TaskStatusChangedEvent evt, CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.All.SendAsync(StatusChangedMethod, evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "推送 taskStatusChanged 失败：MediaItemId={MediaItemId}", evt.MediaItemId);
        }
    }

    public async Task NotifyQueueChangedAsync(QueueChangedEvent evt, CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.All.SendAsync(QueueChangedMethod, evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "推送 queueChanged 失败");
        }
    }
}
