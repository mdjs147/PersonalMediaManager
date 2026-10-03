using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>AI 套餐配额计量实现 — ExecuteUpdate 原子自增 + 条件化幂等置 QuotaExceededAt</summary>
/// <remarks>
/// 实现细节（模式仿 <see cref="AiProviderHealthTracker"/>：IDbContextFactory + IClock，每次独立 DbContext）：
/// - 自增走 ExecuteUpdateAsync 直发 UPDATE（不经 ChangeTracker / RowVersion），避免「读改写」竞态与并发令牌冲突；
///   同一 DbContext 内自增 → 读回 → 置位三步全部顺序 await，不并发（遵守 §八 红线）
/// - 超限置位用条件化 ExecuteUpdate（仅 QuotaExceededAt IS NULL 的行才更新）：并发两笔调用同时越线时
///   只有一笔置位成功（affected=1），日志与告警只发一次（幂等防双发）
/// - 告警经 IAlertService（复用抑制窗口，自身吞异常不阻断解析/对话主流程）；alertKey 按 provider 粒度隔离
/// - QuotaExpiresAt（套餐到期）不在此评估：到期禁用是纯查询过滤，由 AiProviderResolver 剔除
/// </remarks>
internal sealed class AiProviderQuotaTracker : IAiProviderQuotaTracker, IAiProviderQuotaReservationTracker
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly IAlertService _alert;
    private readonly ILogger<AiProviderQuotaTracker> _logger;

    public AiProviderQuotaTracker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IClock clock,
        IAlertService alert,
        ILogger<AiProviderQuotaTracker> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _alert = alert;
        _logger = logger;
    }

    public async Task RecordUsageAsync(long providerId, int? promptTokens, int? completionTokens, CancellationToken ct = default)
    {
        long tokens = (long)(promptTokens ?? 0) + (completionTokens ?? 0);
        DateTimeOffset now = _clock.UtcNow;

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        // 读周期额度配置（判断是否需要周期记账 + 现算下一边界）；provider 已删则直接返回
        var periodCfg = await ctx.ParseAiProviders.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => new { p.QuotaPeriod, p.QuotaPeriodTimeZone })
            .FirstOrDefaultAsync(ct);
        if (periodCfg is null) return;

        // 第一步：原子自增两个终身 Used 计数器（provider 已被删除时 affected=0，直接返回）
        int updated = await ctx.ParseAiProviders
            .Where(p => p.Id == providerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.QuotaUsedCalls, p => p.QuotaUsedCalls + 1)
                .SetProperty(p => p.QuotaUsedTokens, p => p.QuotaUsedTokens + tokens), ct);
        if (updated == 0) return;

        // 第一步b：周期计数（仅 QuotaPeriod≠None 启用时）——now≥ResetAt（跨窗口）或 ResetAt==null（首次）则归零重置并落定新边界，
        // 否则窗口内自增。周期超限不写 QuotaExceededAt：软禁用由 AiProviderResolver 滚动窗口过滤，跨窗口自动恢复，无需告警/人工解除。
        // 单 ExecuteUpdate + CASE 保持原子（不读改写，遵守 §八 同 DbContext 不并发红线）；newBoundary 按本行 period/tz 在 C# 侧现算。
        if (periodCfg.QuotaPeriod != AiQuotaPeriod.None)
        {
            DateTimeOffset newBoundary = QuotaPeriodMath.NextBoundary(now, periodCfg.QuotaPeriod, periodCfg.QuotaPeriodTimeZone);
            await ctx.ParseAiProviders
                .Where(p => p.Id == providerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.QuotaPeriodUsedCalls,
                        p => (p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now) ? 1L : p.QuotaPeriodUsedCalls + 1)
                    .SetProperty(p => p.QuotaPeriodUsedTokens,
                        p => (p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now) ? tokens : p.QuotaPeriodUsedTokens + tokens)
                    .SetProperty(p => p.QuotaPeriodResetAt,
                        p => (p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now) ? (DateTimeOffset?)newBoundary : p.QuotaPeriodResetAt), ct);
        }

        await EvaluateAsync(ctx, providerId, now, ct);
    }

    public async Task<AiProviderQuotaReservation?> TryReserveCallAsync(long providerId, CancellationToken ct = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        var config = await ctx.ParseAiProviders.AsNoTracking().Where(p => p.Id == providerId)
            .Select(p => new { p.QuotaPeriod, p.QuotaPeriodTimeZone, p.QuotaPeriodResetAt }).FirstOrDefaultAsync(ct);
        if (config is null) return null;
        bool periodic = config.QuotaPeriod != AiQuotaPeriod.None;
        DateTimeOffset boundary = periodic && config.QuotaPeriodResetAt > now ? config.QuotaPeriodResetAt.Value
            : periodic ? QuotaPeriodMath.NextBoundary(now, config.QuotaPeriod, config.QuotaPeriodTimeZone) : now;
        // 条件和计次在同一 SQL UPDATE 内完成：独立 scope、拆批及回退均不能复用剩余额度。
        int changed = await ctx.ParseAiProviders.Where(p => p.Id == providerId && p.Enabled
            && (p.DisabledUntil == null || p.DisabledUntil <= now)
            && p.QuotaExceededAt == null && (p.QuotaExpiresAt == null || p.QuotaExpiresAt > now)
            && (p.QuotaCallLimit == null || p.QuotaUsedCalls < p.QuotaCallLimit)
            && (p.QuotaTokenLimit == null || p.QuotaUsedTokens < p.QuotaTokenLimit)
            && p.QuotaPeriod == config.QuotaPeriod && p.QuotaPeriodTimeZone == config.QuotaPeriodTimeZone
            && p.QuotaPeriodResetAt == config.QuotaPeriodResetAt
            && (!periodic || p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now
                || ((p.QuotaPeriodCallLimit == null || p.QuotaPeriodUsedCalls < p.QuotaPeriodCallLimit)
                    && (p.QuotaPeriodTokenLimit == null || p.QuotaPeriodUsedTokens < p.QuotaPeriodTokenLimit))))
            .ExecuteUpdateAsync(update => update
                .SetProperty(p => p.QuotaUsedCalls, p => p.QuotaUsedCalls + 1)
                .SetProperty(p => p.QuotaPeriodUsedCalls, p => !periodic ? p.QuotaPeriodUsedCalls
                    : p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now ? 1 : p.QuotaPeriodUsedCalls + 1)
                .SetProperty(p => p.QuotaPeriodUsedTokens, p => periodic && (p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now)
                    ? 0 : p.QuotaPeriodUsedTokens)
                .SetProperty(p => p.QuotaPeriodResetAt, p => periodic && (p.QuotaPeriodResetAt == null || p.QuotaPeriodResetAt <= now)
                    ? (DateTimeOffset?)boundary : p.QuotaPeriodResetAt), ct);
        return changed == 1 ? new(config.QuotaPeriod, periodic ? boundary : null) : null;
    }

    public async Task SettleTokensAsync(long providerId, AiProviderQuotaReservation reservation, int? promptTokens, int? completionTokens, CancellationToken ct = default)
    {
        long tokens = Math.Max(0, (long)(promptTokens ?? 0)) + Math.Max(0, (long)(completionTokens ?? 0));
        DateTimeOffset now = _clock.UtcNow;
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        // 调用次数已在发送前持久化，响应及取消续行只补 token，不能再次加次数。
        await ctx.ParseAiProviders.Where(p => p.Id == providerId).ExecuteUpdateAsync(update => update
            .SetProperty(p => p.QuotaUsedTokens, p => p.QuotaUsedTokens + tokens)
            .SetProperty(p => p.QuotaPeriodUsedTokens, p => reservation.Period != AiQuotaPeriod.None
                && p.QuotaPeriod == reservation.Period && p.QuotaPeriodResetAt == reservation.PeriodResetAt
                ? p.QuotaPeriodUsedTokens + tokens : p.QuotaPeriodUsedTokens), ct);
        await EvaluateAsync(ctx, providerId, now, ct);
    }

    private async Task EvaluateAsync(PmmDbContext ctx, long providerId, DateTimeOffset now, CancellationToken ct)
    {
        // 第二步：读回该行评估超限（未配置限额 = 不限，永不置位）
        var row = await ctx.ParseAiProviders.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => new
            {
                p.Name,
                p.QuotaCallLimit,
                p.QuotaTokenLimit,
                p.QuotaUsedCalls,
                p.QuotaUsedTokens,
                p.QuotaExceededAt,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return;

        bool exceeded =
            (row.QuotaCallLimit.HasValue && row.QuotaUsedCalls >= row.QuotaCallLimit.Value)
            || (row.QuotaTokenLimit.HasValue && row.QuotaUsedTokens >= row.QuotaTokenLimit.Value);
        if (!exceeded || row.QuotaExceededAt is not null) return;

        // 第三步：条件化幂等置位（仅原值 null 的行才更新，防并发双发）；置位成功才写日志 + 发告警
        int marked = await ctx.ParseAiProviders
            .Where(p => p.Id == providerId && p.QuotaExceededAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.QuotaExceededAt, now), ct);
        if (marked == 0) return;

        _logger.LogWarning(
            "AI 提供商「{Provider}」套餐用量已超限（调用 {UsedCalls}/{CallLimit}，token {UsedTokens}/{TokenLimit}），已自动禁用；放宽限额或重置套餐用量后可恢复",
            row.Name,
            row.QuotaUsedCalls, row.QuotaCallLimit?.ToString() ?? "不限",
            row.QuotaUsedTokens, row.QuotaTokenLimit?.ToString() ?? "不限");

        await _alert.RaiseAsync(
            $"{WebhookEvents.AiProviderQuotaExceeded}:{providerId}",
            WebhookEvents.AiProviderQuotaExceeded,
            new
            {
                providerId,
                providerName = row.Name,
                quotaUsedCalls = row.QuotaUsedCalls,
                quotaUsedTokens = row.QuotaUsedTokens,
                quotaCallLimit = row.QuotaCallLimit,
                quotaTokenLimit = row.QuotaTokenLimit,
            }, ct);
    }
}
