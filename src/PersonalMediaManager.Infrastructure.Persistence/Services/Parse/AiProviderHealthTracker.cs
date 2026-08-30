using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.AiProviders;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>AI 健康追踪实现（D3.4）— 滚动 10min ≥5 失败 → DisabledUntil = now+30min</summary>
/// <remarks>
/// 实现细节：
/// - WindowMinutes / FailureThreshold / CooldownMinutes 为常量（与需求文档 §3.3.3 一致）
/// - 已禁用（DisabledUntil&gt;now）的行不重置：避免连续失败导致禁用时长无限延长（手动 /enable 可立刻解禁）
/// - 评估失败次数走 Audit_AiCall (ProviderId, Success=false, Timestamp&gt;=cutoff) 复合索引覆盖
/// - 失败计数 SaveChanges 写禁用窗口；并发场景下两次评估同时触发也是幂等（DisabledUntil 都会算到相近值）
/// </remarks>
internal sealed class AiProviderHealthTracker : IAiProviderHealthTracker
{
    public const int WindowMinutes = 10;
    public const int FailureThreshold = 5;
    public const int CooldownMinutes = 30;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public AiProviderHealthTracker(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task EvaluateAsync(long providerId, CancellationToken ct = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset windowStart = now.AddMinutes(-WindowMinutes);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        ParseAiProvider? provider = await ctx.ParseAiProviders.FirstOrDefaultAsync(p => p.Id == providerId, ct);
        if (provider is null) return;
        if (provider.DisabledUntil.HasValue && provider.DisabledUntil.Value > now) return;

        int failures = await ctx.AuditAiCalls
            .Where(a => a.ProviderId == providerId && !a.Success && a.Timestamp >= windowStart)
            .CountAsync(ct);

        if (failures < FailureThreshold) return;

        provider.DisabledUntil = now.AddMinutes(CooldownMinutes);
        await ctx.SaveChangesAsync(ct);
    }
}
