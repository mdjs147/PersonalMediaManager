using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Infrastructure.Persistence.Interceptors;

/// <summary>SaveChanges 拦截器：自动维护 IHasTimestamps 实体的 CreatedAt / UpdatedAt</summary>
/// <remarks>
/// Added → CreatedAt = UpdatedAt = now（UTC）；Modified → UpdatedAt = now（不动 CreatedAt）。
/// 委托可注入 IClock（B1.6）后切换；阶段 A 默认用 DateTimeOffset.UtcNow。
/// </remarks>
public sealed class TimestampInterceptor : SaveChangesInterceptor
{
    private readonly Func<DateTimeOffset> _nowProvider;

    public TimestampInterceptor() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>测试可注入固定时间</summary>
    public TimestampInterceptor(Func<DateTimeOffset> nowProvider)
    {
        _nowProvider = nowProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyTimestamps(DbContext? context)
    {
        if (context is null) return;
        DateTimeOffset now = _nowProvider();

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IHasTimestamps stamped) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    stamped.CreatedAt = now;
                    stamped.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    stamped.UpdatedAt = now;
                    // 防御性：保护 CreatedAt 不被外部修改
                    entry.Property(nameof(IHasTimestamps.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }
}
