using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Classify;

/// <summary>分类服务（D7.3）— 规则评估 + 兜底 SendToReview</summary>
/// <remarks>
/// 需求文档 §3.5.2：规则优先 + AI 兜底 + 人工确认三档。
/// 本期实现「规则 + SendToReview」两档：规则覆盖 95% 场景已经够用 MVP；
/// AI 兜底分支待后续单独子任务接入（需要新建 IAiClassifyCoordinator 契约 + ChatModel 调用）。
///
/// 流程：
///   1. 校验 MediaItem 必须有 TmdbId + TmdbMediaType（否则抛 BusinessException —— 编排器调用时已转 Classifying，
///      理应已有 TMDB 候选；走到这里却空说明编排器有 bug）
///   2. 从 Tmdb_MetadataCache 加载元数据；缓存缺失则按需求文档 §3.4 走 ITmdbSearchService.GetDetailsAsync 拉一次（会自动落库）
///   3. 构造 ClassifyContext（MediaType / OriginalLanguage / OriginCountries / Genres）
///   4. 加载所有 Enabled=true 的 CategoryMatchRule，按 (Priority asc, Id asc) 排序
///   5. 调 CategoryRuleEvaluator 评估，命中即返回 Matched(CategoryId)
///   6. 全零命中 → SendToReview（让用户在 AwaitingReview 队列手动选）
///
/// Genres JSON 解析：TMDB 返回 [{"id":18,"name":"Drama"}]，只提取 name 字段；解析失败 → 空列表（不抛）。
/// </remarks>
internal sealed class ClassifyService : IClassifyService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly ITmdbSearchService _tmdb;
    private readonly ILogger<ClassifyService> _logger;

    public ClassifyService(
        IDbContextFactory<PmmDbContext> dbFactory,
        ITmdbSearchService tmdb,
        ILogger<ClassifyService> logger)
    {
        _dbFactory = dbFactory;
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<ClassifyResult> ClassifyAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.TmdbId is null || string.IsNullOrEmpty(item.TmdbMediaType))
        {
            throw new BusinessException("MediaItem 缺少 TmdbId / TmdbMediaType，无法分类");
        }

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        TmdbMetadataCache? meta = await db.TmdbMetadataCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TmdbId == item.TmdbId && c.MediaType == item.TmdbMediaType, ct);

        if (meta is null)
        {
            _logger.LogDebug("Tmdb_MetadataCache 未命中，按需拉取详情：TmdbId={TmdbId}, MediaType={Type}",
                item.TmdbId, item.TmdbMediaType);
            // 触发 ITmdbSearchService.GetDetailsAsync（实现会写缓存），再次查
            await _tmdb.GetDetailsAsync(item.TmdbId.Value, item.TmdbMediaType, ct);
            meta = await db.TmdbMetadataCaches
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TmdbId == item.TmdbId && c.MediaType == item.TmdbMediaType, ct);
        }

        ClassifyContext ctx = BuildContext(item.TmdbMediaType, meta);

        List<CategoryMatchRule> rules = await db.CategoryMatchRules
            .AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);

        foreach (CategoryMatchRule rule in rules)
        {
            if (CategoryRuleEvaluator.Evaluate(rule.Conditions, ctx))
            {
                _logger.LogInformation(
                    "分类规则命中：MediaItemId={MediaItemId}, CategoryId={CategoryId}, RuleId={RuleId}",
                    item.Id, rule.CategoryId, rule.Id);
                return new ClassifyResult(rule.CategoryId, ClassifyDecision.Matched);
            }
        }

        _logger.LogInformation("分类规则全零命中，转 SendToReview：MediaItemId={MediaItemId}", item.Id);
        return new ClassifyResult(null, ClassifyDecision.SendToReview);
    }

    private static ClassifyContext BuildContext(string mediaType, TmdbMetadataCache? meta)
    {
        if (meta is null)
        {
            return new ClassifyContext(mediaType, OriginalLanguage: null,
                OriginCountries: Array.Empty<string>(), Genres: Array.Empty<string>());
        }
        return new ClassifyContext(
            MediaType: mediaType,
            OriginalLanguage: meta.OriginalLanguage,
            OriginCountries: meta.OriginCountry ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Genres: ParseGenres(meta.Genres));
    }

    /// <summary>解析 [{"id":18,"name":"Drama"}, ...] → ["Drama", ...]；失败返回空列表</summary>
    internal static IReadOnlyList<string> ParseGenres(string? genresJson)
    {
        if (string.IsNullOrWhiteSpace(genresJson)) return Array.Empty<string>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(genresJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            List<string> names = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("name", out JsonElement nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && nameEl.GetString() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
            return names;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
