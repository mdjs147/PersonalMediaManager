using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Tmdb;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

/// <summary>TMDB 配置服务实现（单例 Id=1）</summary>
/// <remarks>
/// 种子：Migration HasData 注入 Id=1 一条记录，所以 GetAsync 永远能拿到非 null 实体。
/// Update：始终写 Id=1；ApiKey 三态 null=不变 / ""=清空 / 非空=Protect。
///   Language / FallbackLanguage 变更时自动清空两张 TMDB 缓存表（详见 UpdateAsync 内注释）。
/// /test：读 Id=1 → 解密 ApiKey → 委托 ITmdbConnectivityTester。
/// /cache/clear：ExecuteDeleteAsync 一次性清两张缓存表，返回删除条数（便于前端展示）。
/// </remarks>
internal sealed class TmdbSettingService : ITmdbSettingService
{
    private const long SingletonId = 1;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly ITmdbConnectivityTester _tester;
    private readonly ILogger<TmdbSettingService> _logger;

    public TmdbSettingService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        ITmdbConnectivityTester tester,
        ILogger<TmdbSettingService> logger)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _tester = tester;
        _logger = logger;
    }

    public async Task<TmdbSettingResponse> GetAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting entity = await LoadOrThrowAsync(ctx, ct);
        return ToResponse(entity);
    }

    public async Task<TmdbSettingResponse> UpdateAsync(UpdateTmdbSettingRequest req, CancellationToken ct = default)
    {
        // A 类无状态字段校验（语言非空/长度、数值区间、单权重区间）已迁到 UpdateTmdbSettingRequest 注解，
        // 由 [ApiController] 模型绑定阶段统一校验；此处仅保留跨字段规则「四项权重之和≈1」。
        ValidateWeights(req);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting entity = await LoadOrThrowAsync(ctx, ct);

        // 语言变更检测（修复「改语言后元数据缓存 24h 内吃旧语言」）：
        // Tmdb_MetadataCache 键 (TmdbId, MediaType) 不含语言维度，改语言后旧语言条目会继续命中到 TTL 过期；
        // 给 DB 键加语言列涉及索引/迁移（红线），故走零迁移退路：检测到变更即自动清空两张缓存表。
        // 大小写差异不算变更（TMDB 语言标签大小写不敏感），避免无谓清缓存。
        string oldLanguage = entity.Language;
        string oldFallbackLanguage = entity.FallbackLanguage;
        string newLanguage = req.Language.Trim();
        string newFallbackLanguage = req.FallbackLanguage.Trim();
        bool languageChanged =
            !string.Equals(oldLanguage, newLanguage, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(oldFallbackLanguage, newFallbackLanguage, StringComparison.OrdinalIgnoreCase);

        if (req.ApiKey is not null)
            entity.ApiKeyEncrypted = req.ApiKey.Length == 0 ? null : _protector.Protect(req.ApiKey);
        entity.Language = newLanguage;
        entity.FallbackLanguage = newFallbackLanguage;
        entity.CandidateThreshold = req.CandidateThreshold;
        entity.RateLimitPerSecond = req.RateLimitPerSecond;
        entity.MetadataCacheHours = req.MetadataCacheHours;
        entity.SearchCacheMinutes = req.SearchCacheMinutes;
        entity.ScoreWeightTitle = req.ScoreWeightTitle;
        entity.ScoreWeightYear = req.ScoreWeightYear;
        entity.ScoreWeightPopularity = req.ScoreWeightPopularity;
        entity.ScoreWeightLanguage = req.ScoreWeightLanguage;

        await ctx.SaveChangesAsync(ct);

        if (languageChanged)
        {
            // 复用既有清缓存能力（两表 ExecuteDeleteAsync）；置于 SaveChanges 之后，确保新语言已落库再清
            ClearTmdbCacheResponse cleared = await ClearCacheAsync(ct);
            _logger.LogInformation(
                "TMDB 语言设置变更（{OldLanguage}/{OldFallback} → {NewLanguage}/{NewFallback}），已自动清空缓存：搜索 {SearchRows} 条、元数据 {MetadataRows} 条",
                oldLanguage, oldFallbackLanguage, newLanguage, newFallbackLanguage,
                cleared.SearchRowsDeleted, cleared.MetadataRowsDeleted);
        }

        return ToResponse(entity);
    }

    public async Task<TestTmdbResponse> TestAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting entity = await LoadOrThrowAsync(ctx, ct);
        if (string.IsNullOrEmpty(entity.ApiKeyEncrypted))
            throw new BusinessException("尚未配置 TMDB ApiKey");

        string apiKey = _protector.Unprotect(entity.ApiKeyEncrypted);
        TmdbConnectivityTestResult result = await _tester.TestAsync(apiKey, timeoutSeconds: 30, ct);
        return new TestTmdbResponse(result.Success, result.HttpStatus, result.ElapsedMilliseconds, result.ErrorMessage);
    }

    public async Task<ClearTmdbCacheResponse> ClearCacheAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        int searchDeleted = await ctx.TmdbSearchCaches.ExecuteDeleteAsync(ct);
        int metaDeleted = await ctx.TmdbMetadataCaches.ExecuteDeleteAsync(ct);
        return new ClearTmdbCacheResponse(searchDeleted, metaDeleted);
    }

    private static async Task<TmdbSetting> LoadOrThrowAsync(PmmDbContext ctx, CancellationToken ct)
    {
        TmdbSetting? entity = await ctx.TmdbSettings.FirstOrDefaultAsync(s => s.Id == SingletonId, ct);
        if (entity is null)
            throw new BusinessException("TMDB 配置单例（Id=1）缺失，请检查数据库 Migration 种子");
        return entity;
    }

    private static TmdbSettingResponse ToResponse(TmdbSetting s) =>
        new(!string.IsNullOrEmpty(s.ApiKeyEncrypted),
            s.Language, s.FallbackLanguage,
            s.CandidateThreshold, s.RateLimitPerSecond,
            s.MetadataCacheHours, s.SearchCacheMinutes,
            s.ScoreWeightTitle, s.ScoreWeightYear, s.ScoreWeightPopularity, s.ScoreWeightLanguage,
            s.CreatedAt, s.UpdatedAt);

    /// <summary>跨字段规则：四项权重之和须≈1.0（单字段 [0,1] 区间已由 DTO 注解承接）</summary>
    private static void ValidateWeights(UpdateTmdbSettingRequest req)
    {
        double sum = req.ScoreWeightTitle + req.ScoreWeightYear + req.ScoreWeightPopularity + req.ScoreWeightLanguage;
        if (Math.Abs(sum - 1.0) > 0.001)
            throw new BusinessException($"四项 ScoreWeight 之和必须 ≈ 1.0（当前 {sum:0.###}）");
    }
}
