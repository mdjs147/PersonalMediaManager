using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

/// <summary>Audit_AiCall 留存清理实现（按天龄 + 单 provider 行数上限双闸）</summary>
/// <remarks>
/// 双 ctx 模式：每次 Purge 起独立短期 ctx（红线：同 ctx 不并发）。
/// 闸一 ExecuteDeleteAsync 直删不经变更跟踪；闸二取最旧 Id 列表再批删（家用规模可忽略性能）。
/// 配置缺失 / 非法 / 负数回退默认；置 0 表示停用该闸（不清理）。
/// </remarks>
internal sealed class AiCallRetentionService : IAiCallRetentionService
{
    /// <summary>保留天数 Key（与 SystemSettingConfig 种子 1:1 对齐）</summary>
    public const string RetentionDaysKey = "Audit_AiCallRetentionDays";

    /// <summary>单 provider 行数上限 Key（与 SystemSettingConfig 种子 1:1 对齐）</summary>
    public const string MaxRowsPerProviderKey = "Audit_AiCallMaxRowsPerProvider";

    public const int DefaultRetentionDays = 90;
    public const int DefaultMaxRowsPerProvider = 50_000;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public AiCallRetentionService(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        int retentionDays = await ReadIntAsync(ctx, RetentionDaysKey, DefaultRetentionDays, ct);
        int maxRows = await ReadIntAsync(ctx, MaxRowsPerProviderKey, DefaultMaxRowsPerProvider, ct);

        int deleted = 0;

        // 闸一：按天龄清理
        if (retentionDays > 0)
        {
            DateTimeOffset cutoff = _clock.UtcNow.AddDays(-retentionDays);
            deleted += await ctx.AuditAiCalls.Where(a => a.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        }

        // 闸二：单 provider 行数上限，超额删最旧
        if (maxRows > 0)
        {
            List<long> providerIds = await ctx.AuditAiCalls
                .Select(a => a.ProviderId).Distinct().ToListAsync(ct);
            foreach (long pid in providerIds)
            {
                int count = await ctx.AuditAiCalls.CountAsync(a => a.ProviderId == pid, ct);
                int toDelete = count - maxRows;
                if (toDelete <= 0) continue;
                List<long> oldestIds = await ctx.AuditAiCalls
                    .Where(a => a.ProviderId == pid)
                    .OrderBy(a => a.Timestamp).ThenBy(a => a.Id)
                    .Take(toDelete)
                    .Select(a => a.Id)
                    .ToListAsync(ct);
                deleted += await ctx.AuditAiCalls.Where(a => oldestIds.Contains(a.Id)).ExecuteDeleteAsync(ct);
            }
        }

        return deleted;
    }

    /// <summary>读 System_Setting 整数值；缺失/非法/负数回退默认</summary>
    private static async Task<int> ReadIntAsync(PmmDbContext ctx, string key, int fallback, CancellationToken ct)
    {
        string? raw = await ctx.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out int v) && v >= 0 ? v : fallback;
    }
}
