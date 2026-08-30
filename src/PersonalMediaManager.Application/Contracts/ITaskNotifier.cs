using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>任务状态推送契约（D8.3）— 业务层调用，宿主层通过 SignalR 广播给前端</summary>
/// <remarks>
/// 解耦：Application 不引 Microsoft.AspNetCore.SignalR（§0.5 红线），定义此接口；
/// Host 用 IHubContext&lt;TaskHub&gt; 实现，SendAsync 到 /hubs/tasks 客户端。
/// ProcessFileService 在每次 MediaItem.Transition 后调 NotifyStatusChangedAsync；
/// 入队 / 出队等事件由 Worker / Scan 调 NotifyQueueChangedAsync 广播。
/// 实现侧失败（如 Hub 未连接）应仅记 Warning 不抛——通知是「锦上添花」不能影响业务流。
/// </remarks>
public interface ITaskNotifier
{
    /// <summary>MediaItem 状态变化广播</summary>
    Task NotifyStatusChangedAsync(TaskStatusChangedEvent evt, CancellationToken ct = default);

    /// <summary>队列计数变化广播</summary>
    Task NotifyQueueChangedAsync(QueueChangedEvent evt, CancellationToken ct = default);
}

/// <summary>状态变化事件（method = "taskStatusChanged"）</summary>
/// <param name="MediaItemId">主键</param>
/// <param name="FileName">便于前端不查 ID 直接展示</param>
/// <param name="OldStatus">变化前状态</param>
/// <param name="NewStatus">变化后状态</param>
/// <param name="OccurredAt">事件时间（UTC）</param>
public sealed record TaskStatusChangedEvent(
    long MediaItemId,
    string FileName,
    MediaItemStatus OldStatus,
    MediaItemStatus NewStatus,
    DateTimeOffset OccurredAt);

/// <summary>队列计数变化事件（method = "queueChanged"）</summary>
/// <param name="Pending">Detected + Queued</param>
/// <param name="Running">Parsing..Archiving 中间状态</param>
/// <param name="AwaitingReview">人工确认队列</param>
/// <param name="OccurredAt">事件时间（UTC）</param>
public sealed record QueueChangedEvent(
    int Pending,
    int Running,
    int AwaitingReview,
    DateTimeOffset OccurredAt);

/// <summary>NullObject 实现：不推送任何事件</summary>
/// <remarks>
/// 默认注册（AddApplication）；Host 用 Replace 覆盖为 SignalRTaskNotifier。
/// 让 ProcessFileService / 测试无需 mock 即可工作；纯单元测试场景默认拿这个。
/// </remarks>
public sealed class NullTaskNotifier : ITaskNotifier
{
    public Task NotifyStatusChangedAsync(TaskStatusChangedEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyQueueChangedAsync(QueueChangedEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
