using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Infrastructure.Persistence.Interceptors;

/// <summary>SaveChanges 拦截器：AggregateRoot.RowVersion 在 UPDATE 时自动 +1</summary>
/// <remarks>
/// SQLite 无原生 rowversion；走 long + [ConcurrencyCheck] 模式：EF 在 UPDATE 自动 WHERE RowVersion = @old，
/// 拦截器把当前实例的 RowVersion +1 后写入新值。若并发更新同一行，第二个 UPDATE 影响 0 行 → DbUpdateConcurrencyException。
/// </remarks>
public sealed class RowVersionInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        BumpRowVersions(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        BumpRowVersions(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void BumpRowVersions(DbContext? context)
    {
        if (context is null) return;

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not AggregateRoot root) continue;
            if (entry.State != EntityState.Modified) continue;

            // 取当前 RowVersion +1，EF 会自动将旧值带入 WHERE，新值写入 SET
            PropertyEntry rvEntry = entry.Property(nameof(AggregateRoot.RowVersion));
            rvEntry.CurrentValue = root.RowVersion + 1;
        }
    }
}
