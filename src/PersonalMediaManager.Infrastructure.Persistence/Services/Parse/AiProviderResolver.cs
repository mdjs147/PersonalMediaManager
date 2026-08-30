using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>AI 提供商解析实现（D3.2）— 按 IsPrimary + Priority 输出升级序列 + 跳过不可用行 + 解析满意度阈值</summary>
/// <remarks>
/// 实现细节：
/// - 每次按需查 DB（不缓存）：CRUD 写入立即生效；DisabledUntil / QuotaExpiresAt 时间漂移也立即被剔除
/// - 用 IClock 取 now，便于单测
/// - ApiKeyEncrypted 在此层解密为明文，封装进 AiProviderEndpoint 返回；调用方（D3.3 Orchestrator）不再接触加密细节
/// - 满意度阈值：per-provider ConfidenceThreshold 为空时回退全局 System_Setting[Parse.AiConfidenceThreshold]（默认 0.7）；
///   与 providers 查询用同一 DbContext 顺序执行（不并发，遵守 §八 红线）
/// - 不可用过滤三态并列：DisabledUntil&gt;now（健康熔断，到点自动恢复）、QuotaExceededAt 非空（配额禁用，
///   放宽限额 / reset-quota 才解除）、QuotaExpiresAt≤now（套餐到期，纯时间过滤无标记）；Enabled=false 在 SQL 层过滤
/// - IsPrimary 行不可见（!Enabled 或命中上述任一过滤）时直接跳到下一级，不留空位
/// - 全部 provider 不可用时返回空列表，调用方按需求文档 §3.3.2 决定走人工
/// 读路径用 AsNoTracking 避免污染 DbContext；同实例 DbContext 不并发查询（§八 红线）。
/// </remarks>
internal sealed class AiProviderResolver : IAiProviderResolver
{
    /// <summary>全局兜底满意度阈值的 System_Setting Key（与 SystemSettingConfig 种子 1:1 对齐）</summary>
    private const string GlobalThresholdKey = "Parse.AiConfidenceThreshold";

    /// <summary>全局设置缺失 / 非法时的硬兜底阈值</summary>
    private const double FallbackThreshold = 0.7;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly IClock _clock;

    public AiProviderResolver(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        IClock clock)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AiProviderResolution>> ResolveOrderedAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        DateTimeOffset now = _clock.UtcNow;

        // 全局兜底阈值（per-provider 阈值为空时使用）；与 providers 查询顺序执行，同 DbContext 不并发
        string? globalRaw = await ctx.SystemSettings.AsNoTracking()
            .Where(s => s.Key == GlobalThresholdKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        double globalThreshold = ParseThreshold(globalRaw) ?? FallbackThreshold;

        // 一次查全部 Enabled=true 行，内存中按 IsPrimary desc, Priority asc, Id asc 排序后过滤不可用行
        List<ParseAiProvider> rows = await ctx.ParseAiProviders.AsNoTracking()
            .Where(p => p.Enabled)
            .OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Priority).ThenBy(p => p.Id)
            .ToListAsync(ct);

        List<AiProviderResolution> result = new(rows.Count);
        foreach (ParseAiProvider p in rows)
        {
            if (p.DisabledUntil.HasValue && p.DisabledUntil.Value > now) continue;
            // 配额禁用（用量超限，放宽限额 / reset-quota 才解除，手动 /enable 不绕过）
            if (p.QuotaExceededAt is not null) continue;
            // 套餐到期（纯时间过滤，不写标记；前端按 QuotaExpiresAt 展示状态）
            if (p.QuotaExpiresAt.HasValue && p.QuotaExpiresAt.Value <= now) continue;
            // 周期额度软禁用（滚动窗口）：仅当「仍在当前窗口内」（now < ResetAt）且已达周期上限才跳过；
            // now ≥ ResetAt（已跨窗口）视同用量归零放行，实际清零由下次 RecordUsage 惰性完成——故周期超限跨窗口自动恢复，无需人工解除。
            if (p.QuotaPeriod != AiQuotaPeriod.None
                && p.QuotaPeriodResetAt.HasValue && now < p.QuotaPeriodResetAt.Value
                && ((p.QuotaPeriodCallLimit.HasValue && p.QuotaPeriodUsedCalls >= p.QuotaPeriodCallLimit.Value)
                    || (p.QuotaPeriodTokenLimit.HasValue && p.QuotaPeriodUsedTokens >= p.QuotaPeriodTokenLimit.Value)))
                continue;

            string? apiKey = string.IsNullOrEmpty(p.ApiKeyEncrypted)
                ? null
                : _protector.Unprotect(p.ApiKeyEncrypted);
            // 成本档位 → 节流豁免（免费档），结构化 JSON 与扩展参数原文透传给协议策略
            AiProviderEndpoint endpoint = new(
                p.BaseUrl, apiKey, p.Model, p.TimeoutSeconds, p.UseProxy,
                IsFree: p.CostTier == AiCostTier.Free,
                StructuredJson: p.StructuredJson,
                ExtraOptions: p.ExtraOptions);
            // per-provider 阈值优先；为空回退全局；最终钳到 [0, 1]
            double threshold = Math.Clamp(p.ConfidenceThreshold ?? globalThreshold, 0, 1);
            result.Add(new AiProviderResolution(p.Id, p.Type, p.Name, p.IsPrimary, endpoint, threshold, p.RpmLimit));
        }
        return result;
    }

    /// <summary>解析阈值字符串（InvariantCulture，[0,1] 外或非法返回 null 让调用方回退）</summary>
    private static double? ParseThreshold(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        if (v < 0 || v > 1) return null;
        return v;
    }
}
