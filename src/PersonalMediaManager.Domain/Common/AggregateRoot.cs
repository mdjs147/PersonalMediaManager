namespace PersonalMediaManager.Domain.Common;

/// <summary>聚合根基类（含乐观并发 + 领域事件）</summary>
/// <remarks>
/// 仅聚合根继承本类；普通 Entity 不带 RowVersion。
/// RowVersion 采用 long 配合 EF [ConcurrencyCheck] + UPDATE WHERE 模式（SQLite 无原生 rowversion）。
/// 领域事件由 SaveChanges 拦截器收集分发；DomainEventCollector 在 EF 拦截器读取后清空。
/// </remarks>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>乐观并发版本号（拦截器写入时 +1）</summary>
    public long RowVersion { get; protected set; }

    /// <summary>聚合内累积的领域事件（拦截器读取后清空）</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseEvent(IDomainEvent @event) => _domainEvents.Add(@event);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
