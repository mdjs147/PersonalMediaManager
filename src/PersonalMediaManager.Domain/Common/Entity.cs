namespace PersonalMediaManager.Domain.Common;

/// <summary>实体基类（按 Id 判等，含拦截器时间戳）</summary>
/// <remarks>
/// 所有持久化实体继承本类；CreatedAt / UpdatedAt 由 EF 拦截器（TimestampInterceptor）在 SaveChanges 时自动维护。
/// 标识相等性：未持久化（Id == 0）的两个实例永远不相等；持久化后按 Id 比较。
/// Id setter 设为 internal 允许 Infrastructure.Persistence 程序集通过 InternalsVisibleTo 在测试/迁移场景显式设置；EF Core 会通过反射写入 backing field。
/// </remarks>
public abstract class Entity : IHasTimestamps
{
    /// <summary>主键</summary>
    public long Id { get; protected internal set; }

    /// <summary>创建时间（UTC，拦截器写入）</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>更新时间（UTC，拦截器写入）</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not Entity other) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        if (Id == 0 || other.Id == 0) return false;
        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        // Id 与类型组合，避免不同实体类型同 Id hash 冲突
        return HashCode.Combine(GetType(), Id);
    }

    public static bool operator ==(Entity? left, Entity? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}
