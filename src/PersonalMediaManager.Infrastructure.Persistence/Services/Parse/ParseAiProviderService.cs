using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>AI 提供商服务实现（CRUD + ApiKey 加密 + /test + /enable + /reset-quota）</summary>
/// <remarks>
/// ApiKey：Create/Update 时若非空 Protect 后存 ApiKeyEncrypted；空字符串视为 null（Ollama 本地部署允许）。
/// 响应只暴露 HasApiKey 布尔，不回显密文/明文。
/// IsPrimary：保存为 true 时同事务把其他行降级为 false（避免出现多个主）。
/// /test：解密 ApiKey 后委托 IAiProviderTester；结果不写库（健康统计在 D 阶段）。
/// /enable：手动清 DisabledUntil（不动 Enabled，让用户保留显式 Enabled=false 的语义；不清 QuotaExceededAt——配额禁用不被健康解禁绕过）。
/// 套餐配额：Update 保存前 ReevaluateQuotaExceeded 统一重评估（放宽且不再超限 → 清 QuotaExceededAt；收紧至超限 → 幂等置位）；
/// /reset-quota 清零 QuotaUsedCalls/QuotaUsedTokens + 清 QuotaExceededAt（新套餐周期）。
/// </remarks>
internal sealed class ParseAiProviderService : IParseAiProviderService
{
    /// <summary>全局兜底满意度阈值的 System_Setting Key（与 SystemSettingConfig 种子 / AiProviderResolver 对齐）</summary>
    private const string GlobalThresholdKey = "Parse.AiConfidenceThreshold";

    /// <summary>全局设置缺失 / 非法时的硬兜底阈值</summary>
    private const double FallbackThreshold = 0.7;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly IAiProviderTester _tester;

    public ParseAiProviderService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        IAiProviderTester tester)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _tester = tester;
    }

    public async Task<IReadOnlyList<ParseAiProviderResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        // 全局兜底阈值（per-provider 阈值为空时回退）：一次读出供 ToResponse 算 EffectiveThreshold；同 ctx 顺序执行不并发
        double globalThreshold = await ReadGlobalThresholdAsync(ctx, ct);
        List<ParseAiProvider> rows = await ctx.ParseAiProviders.AsNoTracking()
            .OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Priority).ThenBy(p => p.Id)
            .ToListAsync(ct);

        // 近 7 天调用概览（列表卡片三项指标）：单次拉窗口内数据内存聚合，家用规模数据量可忽略（同 GetStatsAsync 思路）。
        // 平均延迟仅计成功调用，与详情页 stats 接口保持同口径，避免「列表 vs 监控页」数字打架。
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-7);
        var calls = await ctx.AuditAiCalls.AsNoTracking()
            .Where(a => a.Timestamp >= since)
            .Select(a => new { a.ProviderId, a.Success, a.LatencyMs })
            .ToListAsync(ct);
        Dictionary<long, (int Calls, int? Rate, int? Avg)> statsByProvider = calls
            .GroupBy(a => a.ProviderId)
            .ToDictionary(g => g.Key, g =>
            {
                int total = g.Count();
                int success = g.Count(a => a.Success);
                int[] successLatencies = g.Where(a => a.Success).Select(a => a.LatencyMs).ToArray();
                int? rate = (int)Math.Round(success * 100.0 / total);
                int? avg = successLatencies.Length == 0 ? null : (int)Math.Round(successLatencies.Average());
                return (total, rate, avg);
            });

        return rows.Select(p =>
            statsByProvider.TryGetValue(p.Id, out (int Calls, int? Rate, int? Avg) s)
                ? ToResponse(p, globalThreshold, s.Calls, s.Rate, s.Avg)
                : ToResponse(p, globalThreshold)).ToList();
    }

    public async Task<ParseAiProviderResponse> CreateAsync(CreateParseAiProviderRequest req, CancellationToken ct = default)
    {
        // A 类无状态字段校验（Name/BaseUrl/Model 非空与长度、BaseUrl 为 http(s)、Priority/ConfidenceThreshold/TimeoutSeconds 范围）
        // 已上移至 DTO DataAnnotations，由 [ApiController] 在模型绑定阶段校验；此处仅做 Trim 归一与加密。
        string name = NormalizeName(req.Name);
        string baseUrl = NormalizeBaseUrl(req.BaseUrl);
        string model = NormalizeModel(req.Model);
        // ApiKey 放开：不再因非 Ollama 类型缺 ApiKey 而拒绝（统一可选鉴权，空则不下发 Authorization 头）
        ValidateExtraOptions(req.ExtraOptions);

        // 智能默认：CostTier 为非空值类型且 DTO 默认 Paid，无法区分「显式 Paid」与「未传」；
        // 故按协议纠偏——Ollama 本地节点取到默认 Paid 几乎必是省略（本地无计费），自动预填 Free；
        // 其余协议默认 Paid 即正确，显式 Free 也原样保留。
        AiCostTier costTier = req.Type == AiProviderType.Ollama && req.CostTier == AiCostTier.Paid
            ? AiCostTier.Free
            : req.CostTier;
        // 免费档若未显式给阈值（ConfidenceThreshold 可空，null=未传），默认高阈值 0.85（把握不足即升级到付费档）
        double? confidenceThreshold = req.ConfidenceThreshold;
        if (costTier == AiCostTier.Free && !req.ConfidenceThreshold.HasValue)
            confidenceThreshold = 0.85;

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.ParseAiProviders.AnyAsync(p => p.Name == name, ct))
            throw new BusinessException($"AI 提供商 {name} 已存在");

        if (req.IsPrimary)
            await DemoteExistingPrimaryAsync(ctx, excludeId: 0, ct);

        ParseAiProvider entity = new()
        {
            Name = name,
            Type = req.Type,
            CostTier = costTier,
            StructuredJson = req.StructuredJson,
            BaseUrl = baseUrl,
            ApiKeyEncrypted = EncryptIfPresent(req.ApiKey),
            Model = model,
            IsPrimary = req.IsPrimary,
            Priority = req.Priority,
            ConfidenceThreshold = confidenceThreshold,
            Enabled = req.Enabled,
            TimeoutSeconds = req.TimeoutSeconds,
            ExtraOptions = req.ExtraOptions?.Trim(),
            UseProxy = req.UseProxy,
            // 套餐配额配置（Used 计数器从 0 起，新建行不可能超限，无需重评估）
            QuotaCallLimit = req.QuotaCallLimit,
            QuotaTokenLimit = req.QuotaTokenLimit,
            QuotaExpiresAt = req.QuotaExpiresAt,
            // 周期滚动额度配置（周期 Used 从 0 起、ResetAt 首次 RecordUsage 落定）
            QuotaPeriod = req.QuotaPeriod,
            QuotaPeriodTimeZone = NormalizeTimeZone(req.QuotaPeriodTimeZone),
            QuotaPeriodCallLimit = req.QuotaPeriodCallLimit,
            QuotaPeriodTokenLimit = req.QuotaPeriodTokenLimit,
            // RPM 限流上限（滑动 60 秒；null=不限流）
            RpmLimit = req.RpmLimit,
        };
        ctx.ParseAiProviders.Add(entity);
        await ctx.SaveChangesAsync(ct);
        // 单条返回：读全局阈值算 EffectiveThreshold/ThresholdSource（同 ctx 顺序执行，不并发，遵守 §八 红线）
        double globalThreshold = await ReadGlobalThresholdAsync(ctx, ct);
        return ToResponse(entity, globalThreshold);
    }

    public async Task<ParseAiProviderResponse> UpdateAsync(UpdateParseAiProviderRequest req, CancellationToken ct = default)
    {
        // A 类无状态字段校验同 CreateAsync 已上移 DTO DataAnnotations；此处仅做 Trim 归一与加密。
        string name = NormalizeName(req.Name);
        string baseUrl = NormalizeBaseUrl(req.BaseUrl);
        string model = NormalizeModel(req.Model);
        ValidateExtraOptions(req.ExtraOptions);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseAiProvider? entity = await ctx.ParseAiProviders.FirstOrDefaultAsync(p => p.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"AI 提供商 Id={req.Id} 不存在");

        if (entity.Name != name
            && await ctx.ParseAiProviders.AnyAsync(p => p.Id != req.Id && p.Name == name, ct))
            throw new BusinessException($"AI 提供商 {name} 已存在");

        if (req.IsPrimary && !entity.IsPrimary)
            await DemoteExistingPrimaryAsync(ctx, excludeId: req.Id, ct);

        entity.Name = name;
        entity.Type = req.Type;
        entity.CostTier = req.CostTier;
        entity.StructuredJson = req.StructuredJson;
        entity.BaseUrl = baseUrl;
        // ApiKey 三态：null = 不变；"" = 清空；非空 = 重新加密。ApiKey 放开后清空不再校验类型（统一可选鉴权）
        if (req.ApiKey is not null)
            entity.ApiKeyEncrypted = req.ApiKey.Length == 0 ? null : _protector.Protect(req.ApiKey);
        entity.Model = model;
        entity.IsPrimary = req.IsPrimary;
        entity.Priority = req.Priority;
        entity.ConfidenceThreshold = req.ConfidenceThreshold;
        entity.Enabled = req.Enabled;
        entity.TimeoutSeconds = req.TimeoutSeconds;
        entity.ExtraOptions = req.ExtraOptions?.Trim();
        entity.UseProxy = req.UseProxy;
        // 套餐配额配置为全量语义（null = 清除该限额）；改完立即按当前用量重评估配额禁用标记
        entity.QuotaCallLimit = req.QuotaCallLimit;
        entity.QuotaTokenLimit = req.QuotaTokenLimit;
        entity.QuotaExpiresAt = req.QuotaExpiresAt;
        // 周期额度：切换粒度（含启用/停用/改粒度）时重置本周期计量（清零已用 + ResetAt=null，下次 RecordUsage 按新粒度落定边界）；仅改限额则保留当前计数
        bool periodChanged = entity.QuotaPeriod != req.QuotaPeriod;
        entity.QuotaPeriod = req.QuotaPeriod;
        entity.QuotaPeriodTimeZone = NormalizeTimeZone(req.QuotaPeriodTimeZone);
        entity.QuotaPeriodCallLimit = req.QuotaPeriodCallLimit;
        entity.QuotaPeriodTokenLimit = req.QuotaPeriodTokenLimit;
        entity.RpmLimit = req.RpmLimit;
        if (periodChanged)
        {
            entity.QuotaPeriodUsedCalls = 0;
            entity.QuotaPeriodUsedTokens = 0;
            entity.QuotaPeriodResetAt = null;
        }
        ReevaluateQuotaExceeded(entity, DateTimeOffset.UtcNow);
        await ctx.SaveChangesAsync(ct);
        // 单条返回：读全局阈值算 EffectiveThreshold/ThresholdSource（同 ctx 顺序执行，不并发）
        double globalThreshold = await ReadGlobalThresholdAsync(ctx, ct);
        return ToResponse(entity, globalThreshold);
    }

    public async Task DeleteAsync(DeleteParseAiProviderRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseAiProvider? entity = await ctx.ParseAiProviders.FirstOrDefaultAsync(p => p.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"AI 提供商 Id={req.Id} 不存在");
        ctx.ParseAiProviders.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<TestParseAiProviderResponse> TestAsync(TestParseAiProviderRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseAiProvider? entity = await ctx.ParseAiProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"AI 提供商 Id={req.Id} 不存在");

        string? apiKey = string.IsNullOrEmpty(entity.ApiKeyEncrypted) ? null : _protector.Unprotect(entity.ApiKeyEncrypted);
        AiProviderTestResult result = await _tester.TestAsync(
            entity.Type, entity.BaseUrl, apiKey, entity.Model, entity.TimeoutSeconds, entity.UseProxy, ct);
        return new TestParseAiProviderResponse(
            result.Success, result.HttpStatus, result.ElapsedMilliseconds, result.ErrorMessage, result.ResponseSnippet);
    }

    public async Task EnableAsync(EnableParseAiProviderRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseAiProvider? entity = await ctx.ParseAiProviders.FirstOrDefaultAsync(p => p.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"AI 提供商 Id={req.Id} 不存在");

        // 仅解除健康熔断；配额禁用（QuotaExceededAt）不在此清除——须放宽限额或 reset-quota
        entity.DisabledUntil = null;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ResetQuotaAsync(ResetQuotaParseAiProviderRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseAiProvider? entity = await ctx.ParseAiProviders.FirstOrDefaultAsync(p => p.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"AI 提供商 Id={req.Id} 不存在");

        // 开始新套餐周期：清零累计用量并解除配额禁用；限额配置与 QuotaExpiresAt 保持不变（到期时间由 Update 调整）
        entity.QuotaUsedCalls = 0;
        entity.QuotaUsedTokens = 0;
        entity.QuotaExceededAt = null;
        // 周期计量一并清零（新周期从头计）；ResetAt=null 让下次 RecordUsage 重新落定边界
        entity.QuotaPeriodUsedCalls = 0;
        entity.QuotaPeriodUsedTokens = 0;
        entity.QuotaPeriodResetAt = null;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<AiProviderStatsResponse> GetStatsAsync(long providerId, int windowHours, CancellationToken ct = default)
    {
        if (windowHours < 1 || windowHours > 168)
            throw new BusinessException("windowHours 必须在 [1, 168] 范围内");

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (!await ctx.ParseAiProviders.AsNoTracking().AnyAsync(p => p.Id == providerId, ct))
            throw new BusinessException($"AI 提供商 Id={providerId} 不存在");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset since = now.AddHours(-windowHours);

        // 单次拉满窗口数据；家用规模窗口内数据量可忽略
        List<Domain.Entities.AuditAiCall> rows = await ctx.AuditAiCalls.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.Timestamp >= since && a.Timestamp <= now)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(ct);

        int total = rows.Count;
        int success = rows.Count(a => a.Success);
        int failed = total - success;

        int[] successLatencies = rows.Where(a => a.Success).Select(a => a.LatencyMs).OrderBy(x => x).ToArray();
        double avgLatency = successLatencies.Length == 0 ? 0 : successLatencies.Average();
        int p95Latency = successLatencies.Length == 0 ? 0 : Percentile(successLatencies, 0.95);

        // 小时桶：HoursAgo = floor((now - ts).TotalHours)，0..windowHours-1
        int[] totalsPerHour = new int[windowHours];
        int[] failedPerHour = new int[windowHours];
        foreach (Domain.Entities.AuditAiCall a in rows)
        {
            int h = (int)Math.Floor((now - a.Timestamp).TotalHours);
            if (h < 0 || h >= windowHours) continue;
            totalsPerHour[h]++;
            if (!a.Success) failedPerHour[h]++;
        }
        List<AiCallHourlyBucket> hourly = new(windowHours);
        for (int i = 0; i < windowHours; i++)
            hourly.Add(new AiCallHourlyBucket(i, totalsPerHour[i], failedPerHour[i]));

        // 固定延迟桶（仅 Success；Failed 延迟无业务意义）
        (string Label, int Lower, int? Upper)[] bucketsDef =
        [
            ("0-500ms", 0, 500),
            ("500ms-1s", 500, 1000),
            ("1-2s", 1000, 2000),
            ("2-3s", 2000, 3000),
            ("3-5s", 3000, 5000),
            (">5s", 5000, null),
        ];
        List<AiCallLatencyBucket> latencyHist = bucketsDef.Select(b =>
        {
            int count = successLatencies.Count(l => l >= b.Lower && (b.Upper is null || l < b.Upper.Value));
            return new AiCallLatencyBucket(b.Label, b.Lower, b.Upper, count);
        }).ToList();

        // 错误分布
        List<AiCallErrorBucket> errors = rows
            .Where(a => !a.Success)
            .GroupBy(a => a.ErrorType ?? "Unknown")
            .Select(g => new AiCallErrorBucket(g.Key, g.Count()))
            .OrderByDescending(b => b.Count)
            .ToList();

        // token 合计（成本视角）：厂商未返回 usage 的调用计 0
        int totalPromptTokens = rows.Sum(a => a.PromptTokens ?? 0);
        int totalCompletionTokens = rows.Sum(a => a.CompletionTokens ?? 0);

        // 平均置信度：有置信度记录（含低置信软失败行）才计入；窗口内无任何置信度 → null
        double[] confidences = rows.Where(a => a.Confidence.HasValue).Select(a => a.Confidence!.Value).ToArray();
        double? avgConfidence = confidences.Length == 0 ? null : confidences.Average();

        // 按模型聚合（Model 为空的历史行不计入）：每模型调用数 + token 合计
        List<AiCallModelBucket> modelBreakdown = rows
            .Where(a => !string.IsNullOrEmpty(a.Model))
            .GroupBy(a => a.Model!)
            .Select(g => new AiCallModelBucket(g.Key, g.Count(),
                g.Sum(a => (a.PromptTokens ?? 0) + (a.CompletionTokens ?? 0))))
            .OrderByDescending(b => b.Count)
            .ToList();

        // 最近 12 条（含置信度/错误详情/token/模型/链路，激活监控页明细列）
        List<AiCallRecentEntry> recent = rows.Take(12)
            .Select(a => new AiCallRecentEntry(a.Id, a.MediaItemId, a.Success, a.LatencyMs, a.ErrorType, a.Timestamp,
                a.Confidence, a.ErrorDetail, a.PromptTokens, a.CompletionTokens, a.Model, a.ChainId, a.AttemptLevel))
            .ToList();

        return new AiProviderStatsResponse(
            providerId, windowHours, total, success, failed, avgLatency, p95Latency,
            totalPromptTokens, totalCompletionTokens, avgConfidence,
            hourly, latencyHist, errors, modelBreakdown, recent);
    }

    public async Task<AiCallLogPageResponse> GetLogsAsync(long providerId, AiCallLogQuery q, CancellationToken ct = default)
    {
        int page = Math.Max(1, q.Page);
        int pageSize = Math.Clamp(q.PageSize, 1, 100);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (!await ctx.ParseAiProviders.AsNoTracking().AnyAsync(p => p.Id == providerId, ct))
            throw new BusinessException($"AI 提供商 Id={providerId} 不存在");

        IQueryable<Domain.Entities.AuditAiCall> query = ctx.AuditAiCalls.AsNoTracking()
            .Where(a => a.ProviderId == providerId);
        if (q.Success.HasValue) query = query.Where(a => a.Success == q.Success.Value);
        if (!string.IsNullOrWhiteSpace(q.ErrorType)) query = query.Where(a => a.ErrorType == q.ErrorType);
        if (!string.IsNullOrWhiteSpace(q.ChainId)) query = query.Where(a => a.ChainId == q.ChainId);
        if (q.From.HasValue) query = query.Where(a => a.Timestamp >= q.From.Value);
        if (q.To.HasValue) query = query.Where(a => a.Timestamp <= q.To.Value);

        int total = await query.CountAsync(ct);
        List<AiCallLogEntry> items = await query
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AiCallLogEntry(
                a.Id, a.MediaItemId, a.Success, a.LatencyMs, a.ErrorType, a.ErrorDetail,
                a.Model, a.PromptTokens, a.CompletionTokens, a.Confidence, a.HttpStatus,
                a.ChainId, a.AttemptLevel, a.IsPrimary, a.RequestText, a.ResponseText, a.Timestamp))
            .ToListAsync(ct);

        return new AiCallLogPageResponse(providerId, page, pageSize, total, items);
    }

    /// <summary>线性插值百分位（输入已排序）；空数组返回 0</summary>
    private static int Percentile(int[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double rank = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        double frac = rank - lower;
        return (int)Math.Round(sorted[lower] + frac * (sorted[upper] - sorted[lower]));
    }

    private static async Task DemoteExistingPrimaryAsync(PmmDbContext ctx, long excludeId, CancellationToken ct)
    {
        List<ParseAiProvider> primaries = await ctx.ParseAiProviders
            .Where(p => p.IsPrimary && p.Id != excludeId)
            .ToListAsync(ct);
        foreach (ParseAiProvider p in primaries) p.IsPrimary = false;
    }

    /// <summary>按当前用量与限额重评估配额禁用标记（Update 保存前调用）</summary>
    /// <remarks>
    /// 放宽（限额调高/清除）且不再超限 → 自动清 QuotaExceededAt；收紧至超限 → 幂等置位（原值非空保留首次时刻）。
    /// QuotaExpiresAt（套餐到期）不参与评估：到期禁用是纯查询过滤，无标记可清。
    /// </remarks>
    private static void ReevaluateQuotaExceeded(ParseAiProvider entity, DateTimeOffset now)
    {
        bool exceeded =
            (entity.QuotaCallLimit.HasValue && entity.QuotaUsedCalls >= entity.QuotaCallLimit.Value)
            || (entity.QuotaTokenLimit.HasValue && entity.QuotaUsedTokens >= entity.QuotaTokenLimit.Value);
        if (exceeded)
            entity.QuotaExceededAt ??= now;
        else
            entity.QuotaExceededAt = null;
    }

    /// <summary>实体 → 响应 DTO（含生效阈值与来源计算）</summary>
    /// <remarks>
    /// EffectiveThreshold = 自定义 ConfidenceThreshold 优先，否则回退 <paramref name="globalThreshold"/>（已含免费档高阈值默认，建库时写入 ConfidenceThreshold）；
    /// ThresholdSource = "custom"（用本 provider 自定义值）/ "global"（回退全局或档位默认）。
    /// </remarks>
    private static ParseAiProviderResponse ToResponse(
        ParseAiProvider p, double globalThreshold, int calls7d = 0, int? successRate = null, int? avgLatency = null) =>
        new(p.Id, p.Name, p.Type, p.CostTier, p.StructuredJson, p.BaseUrl, !string.IsNullOrEmpty(p.ApiKeyEncrypted), p.Model,
            p.IsPrimary, p.Priority, p.ConfidenceThreshold,
            p.ConfidenceThreshold ?? globalThreshold,
            p.ConfidenceThreshold.HasValue ? "custom" : "global",
            p.Enabled, p.DisabledUntil,
            p.QuotaCallLimit, p.QuotaTokenLimit, p.QuotaExpiresAt,
            p.QuotaUsedCalls, p.QuotaUsedTokens, p.QuotaExceededAt,
            p.QuotaPeriod, p.QuotaPeriodTimeZone, p.QuotaPeriodCallLimit, p.QuotaPeriodTokenLimit,
            p.QuotaPeriodUsedCalls, p.QuotaPeriodUsedTokens, p.QuotaPeriodResetAt,
            p.RpmLimit,
            p.TimeoutSeconds, p.ExtraOptions,
            p.UseProxy, p.CreatedAt, p.UpdatedAt, calls7d, successRate, avgLatency);

    /// <summary>读全局兜底满意度阈值（System_Setting[Parse.AiConfidenceThreshold]），缺失/非法回退 0.7</summary>
    private static async Task<double> ReadGlobalThresholdAsync(PmmDbContext ctx, CancellationToken ct)
    {
        string? raw = await ctx.SystemSettings.AsNoTracking()
            .Where(s => s.Key == GlobalThresholdKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
            && v is >= 0 and <= 1)
            return v;
        return FallbackThreshold;
    }

    private string? EncryptIfPresent(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    // 非空与长度校验已上移 DTO DataAnnotations，本方法仅保留 Trim 归一
    private static string NormalizeName(string raw) => raw.Trim();

    // 非空、长度、http(s) 校验已上移 DTO（[RequiredNotBlank]/[MaxLength]/[HttpUrl]），本方法仅保留 Trim 归一
    private static string NormalizeBaseUrl(string raw) => raw.Trim();

    // 非空与长度校验已上移 DTO DataAnnotations，本方法仅保留 Trim 归一
    private static string NormalizeModel(string raw) => raw.Trim();

    /// <summary>周期额度时区 id 归一：空白 → null（走本机时区），否则 Trim</summary>
    private static string? NormalizeTimeZone(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    // ExtraOptions 长度上限已上移 DTO（[MaxLength(2000)]）；此处仅保留「必须是合法 JSON」校验（声明式不便携 ex.Message 明细，KEEP）
    private static void ValidateExtraOptions(string? extra)
    {
        if (extra is null) return;
        try
        {
            using System.Text.Json.JsonDocument _ = System.Text.Json.JsonDocument.Parse(extra);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BusinessException($"ExtraOptions 不是合法 JSON：{ex.Message}");
        }
    }
}
