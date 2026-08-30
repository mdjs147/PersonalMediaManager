using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace PersonalMediaManager.Infrastructure.Persistence.Conversions;

/// <summary>JSON 列 ValueComparer（深比较 + 快照）— 可空版本</summary>
/// <remarks>
/// EF ChangeTracker 默认按引用比较，导致集合 / POCO 改写不被检测；本 Comparer 通过序列化对比 + 反序列化快照解决。
/// 性能权衡：每次 SaveChanges 都会序列化两次（原值 + 当前值），适用 JSON 字段 &lt; 4KB 的中小配置场景。
/// 不重写 Equals/Snapshot 方法（避免 CS0114 隐藏继承成员告警）；通过基类 ctor 传入 lambda。
/// </remarks>
public sealed class JsonValueComparer<T> : ValueComparer<T?>
    where T : class
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public JsonValueComparer()
        : base(
            (a, b) => Eq(a, b),
            v => v == null ? 0 : JsonSerializer.Serialize(v, JsonOpts).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOpts), JsonOpts))
    {
    }

    private static bool Eq(T? a, T? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(b, JsonOpts);
    }
}

/// <summary>JSON 列 ValueComparer — 非空版本（用于 WebhookSubscription.Events 等非空集合属性）</summary>
public sealed class JsonValueComparerNonNull<T> : ValueComparer<T>
    where T : class
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public JsonValueComparerNonNull()
        : base(
            (a, b) => JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(b, JsonOpts),
            v => JsonSerializer.Serialize(v, JsonOpts).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOpts), JsonOpts)!)
    {
    }
}
