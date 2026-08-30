namespace PersonalMediaManager.Domain.Common;

/// <summary>领域事件标记接口</summary>
/// <remarks>
/// 阶段 A 仅定义接口；事件分发（IDomainEventDispatcher）放 Application 层，由 SaveChanges 拦截器调用。
/// 事件发生时刻使用 UTC，便于跨时区追踪。
/// </remarks>
public interface IDomainEvent
{
    /// <summary>事件发生时刻（UTC）</summary>
    DateTimeOffset OccurredAt { get; }
}
