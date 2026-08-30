using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

/// <summary>TMDB 搜索 + 详情编排（D2.2）— 两层缓存 + ApiKey/语言/速率注入 + 透明回退链</summary>
/// <remarks>
/// 搜索缓存（Tmdb_SearchCache）：QueryHash = SHA256(query|type|year|language|fallback)，
/// CachedAt + Tmdb_Setting.SearchCacheMinutes 决定有效期；命中时直接还原候选列表，不发请求。
/// 元数据缓存（Tmdb_MetadataCache）：(TmdbId, MediaType) 复合 PK；CachedAt + MetadataCacheHours 决定有效期。
/// ApiKey 解密由 IProtectedFieldService；未配置 ApiKey 时抛 BusinessException → 1000。
/// 写缓存：UPSERT 模式（Tmdb_SearchCache 是 PK，所以先查再 Add/Update；Tmdb_MetadataCache 同理）。
///
/// 语言注入：请求未显式指定 Language/FallbackLanguage（null）时，统一注入 Tmdb_Setting 配置——
/// 调用方（ProcessFileService / DryRunService / ReviewService）从不传语言，旧版因此永远落在 record
/// 默认 zh-CN，配置改了也不生效（「语言死旋钮」）。解析后的语言进入缓存键，改语言自然分键。
///
/// 透明搜索回退链（对调用方完全透明，最多 4 次，逐次走缓存）：
///   ① 主语言带年 → ② 主语言去年份 → ③ 回退语言带年 → ④ 回退语言去年份
/// 原因：跨年播出剧（文件标 2024、TMDB 首播 2023）或 AI 年份偏 1 时，year/first_air_date_year 是
/// 严格过滤会逐层零结果误入人工审核（修复①②）；主语言（多为中文）检索不中时再用 FallbackLanguage
/// 重试（修复③④——旧版 FallbackLanguage 只进缓存键从不真正回退）。每层缓存键含 year+language 互不
/// 污染；零结果同样写缓存，重复任务不会反复打远端。
/// </remarks>
internal sealed class TmdbSearchService : ITmdbSearchService
{
    private const long TmdbSettingId = 1;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly ITmdbClient _client;
    private readonly IPosterDownloader _poster;
    private readonly AppPaths _paths;
    private readonly ILogger<TmdbSearchService> _logger;

    public TmdbSearchService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        ITmdbClient client,
        IPosterDownloader poster,
        AppPaths paths,
        ILogger<TmdbSearchService> logger)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _client = client;
        _poster = poster;
        _paths = paths;
        _logger = logger;
    }

    public async Task<TmdbSearchResult> SearchAsync(TmdbSearchRequest request, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting setting = await LoadSettingAsync(ctx, ct);
        string apiKey = DecryptApiKey(setting);

        // 语言解析：请求显式指定优先，未指定（null/空白）注入 Tmdb_Setting 配置（修复「语言死旋钮」）
        string primaryLanguage = string.IsNullOrWhiteSpace(request.Language) ? setting.Language : request.Language;
        string fallbackLanguage = string.IsNullOrWhiteSpace(request.FallbackLanguage) ? setting.FallbackLanguage : request.FallbackLanguage;

        // 透明回退链：① 主语言带年 → ② 主语言去年份 → ③ 回退语言带年 → ④ 回退语言去年份（详见类 remarks）
        IReadOnlyList<TmdbSearchRequest> attempts = BuildAttemptChain(request, primaryLanguage, fallbackLanguage);

        TmdbSearchResult? firstResult = null;
        for (int i = 0; i < attempts.Count; i++)
        {
            TmdbSearchResult result = await SearchOnceAsync(ctx, setting, apiKey, attempts[i], ct);
            firstResult ??= result;
            if (result.Candidates.Count > 0)
            {
                if (i > 0)
                {
                    _logger.LogInformation(
                        "TMDB 回退搜索命中（第 {Layer} 层：language={Language}, year={Year}）：query={Query}，候选={Count}",
                        i + 1, attempts[i].Language, attempts[i].Year?.ToString() ?? "无", request.Query, result.Candidates.Count);
                }
                return result;
            }
        }

        // 全链零结果：返回首层（主语言带年）的空结果，调用方按既有零候选流程处理（如转人工审核）
        return firstResult!;
    }

    /// <summary>构造搜索回退链（去重：无年份跳过去年份层；回退语言与主语言相同或为空时跳过回退层）</summary>
    private static IReadOnlyList<TmdbSearchRequest> BuildAttemptChain(TmdbSearchRequest request, string primaryLanguage, string fallbackLanguage)
    {
        TmdbSearchRequest primary = request with { Language = primaryLanguage, FallbackLanguage = fallbackLanguage };
        List<TmdbSearchRequest> attempts = [primary];
        if (primary.Year is not null)
            attempts.Add(primary with { Year = null }); // 去年份重搜：宽恕跨年播出/AI 年份偏 1

        bool hasDistinctFallback = !string.IsNullOrWhiteSpace(fallbackLanguage)
            && !string.Equals(fallbackLanguage, primaryLanguage, StringComparison.OrdinalIgnoreCase);
        if (hasDistinctFallback)
        {
            attempts.Add(primary with { Language = fallbackLanguage });
            if (primary.Year is not null)
                attempts.Add(primary with { Language = fallbackLanguage, Year = null });
        }
        return attempts;
    }

    /// <summary>单次搜索（缓存优先；未命中走远端并回写缓存，含零结果）</summary>
    private async Task<TmdbSearchResult> SearchOnceAsync(PmmDbContext ctx, TmdbSetting setting, string apiKey, TmdbSearchRequest request, CancellationToken ct)
    {
        string queryHash = ComputeQueryHash(request);
        DateTimeOffset searchExpiry = DateTimeOffset.UtcNow.AddMinutes(-setting.SearchCacheMinutes);

        TmdbSearchCache? cached = await ctx.TmdbSearchCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.QueryHash == queryHash && c.CachedAt >= searchExpiry, ct);

        if (cached is not null)
        {
            try
            {
                List<TmdbCandidate> restored = JsonSerializer.Deserialize<List<TmdbCandidate>>(cached.Results)
                    ?? [];
                _logger.LogInformation("TMDB 搜索命中本地缓存(DB)，不计远端额度：query={Query}", ToQueryRaw(request));
                return new TmdbSearchResult(restored, cached.Results, FromCache: true);
            }
            catch (JsonException)
            {
                // 缓存损坏 → 当未命中处理，下次会覆盖
            }
        }

        // 限流速率随设置流入客户端（修复「RateLimitPerSecond 死旋钮」）；设置本次调用已加载，零额外查库
        TmdbSearchResult fresh = await _client.SearchAsync(request, apiKey, setting.RateLimitPerSecond, ct);
        _logger.LogInformation("TMDB 搜索远端拉取：query={Query}，候选={Count}", ToQueryRaw(request), fresh.Candidates.Count);
        string candidateJson = JsonSerializer.Serialize(fresh.Candidates);
        await UpsertSearchCacheAsync(ctx, queryHash, ToQueryRaw(request), candidateJson, ct);
        return fresh;
    }

    public async Task<TmdbDetailsResult> GetDetailsAsync(int tmdbId, string mediaType, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting setting = await LoadSettingAsync(ctx, ct);
        string apiKey = DecryptApiKey(setting);

        DateTimeOffset metaExpiry = DateTimeOffset.UtcNow.AddHours(-setting.MetadataCacheHours);
        string normType = mediaType.ToLowerInvariant();

        TmdbMetadataCache? cached = await ctx.TmdbMetadataCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TmdbId == tmdbId && c.MediaType == normType && c.CachedAt >= metaExpiry, ct);

        if (cached is not null && cached.RawJson is not null)
        {
            // 第 11 位是 Overview（此前误填 RawJson，致缓存命中时 Overview 被污染成整段 JSON，一并修正）
            TmdbDetailsResult hit = new(
                cached.TmdbId, cached.MediaType, cached.Title, cached.OriginalTitle, cached.Year,
                cached.TotalSeasons, cached.PosterPath, cached.OriginCountry, cached.OriginalLanguage,
                cached.Genres, cached.Overview, cached.RawJson, TmdbSeasonsParser.Parse(cached.RawJson),
                FromCache: true);
            _logger.LogInformation("TMDB 详情命中本地缓存(DB)，不计远端额度：tmdbId={TmdbId}, type={Type}", tmdbId, normType);
            await TryCachePosterAsync(hit.TmdbId, hit.PosterPath, ct);
            return hit;
        }

        // 限流速率随设置流入客户端（修复「RateLimitPerSecond 死旋钮」）
        TmdbDetailsResult fresh = await _client.GetDetailsAsync(tmdbId, normType, apiKey, setting.Language, setting.RateLimitPerSecond, ct);
        _logger.LogInformation("TMDB 详情远端拉取：tmdbId={TmdbId}, type={Type}", tmdbId, normType);
        await UpsertMetadataCacheAsync(ctx, fresh, ct);
        await TryCachePosterAsync(fresh.TmdbId, fresh.PosterPath, ct);
        return fresh;
    }

    public async Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(string episodeGroupId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episodeGroupId)) throw new BusinessException("剧集组 id 不能为空");
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting setting = await LoadSettingAsync(ctx, ct);
        string apiKey = DecryptApiKey(setting);

        // 复用 Tmdb_SearchCache 表（合成 QueryHash 命名空间 episode_group|，与搜索条目零碰撞），避免为静态的
        // 剧集组单建表/迁移；TTL 取 MetadataCacheHours（剧集组极少变动，按详情缓存的较长有效期即可）。
        // 缓存 Results 存"已解析的 TmdbEpisodeGroup 序列化 JSON"，命中时反序列化还原（与搜索候选同套路）。
        string hash = ComputeEpisodeGroupHash(episodeGroupId, setting.Language);
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(-setting.MetadataCacheHours);
        TmdbSearchCache? cached = await ctx.TmdbSearchCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.QueryHash == hash && c.CachedAt >= expiry, ct);
        if (cached is not null)
        {
            try
            {
                TmdbEpisodeGroup? hit = JsonSerializer.Deserialize<TmdbEpisodeGroup>(cached.Results);
                if (hit is not null)
                {
                    _logger.LogInformation("TMDB 剧集组命中本地缓存(DB)，不计远端额度：{Id}", episodeGroupId);
                    return hit;
                }
            }
            catch (JsonException)
            {
                // 缓存损坏 → 当未命中处理，下面覆盖
            }
        }

        TmdbEpisodeGroup fresh = await _client.GetEpisodeGroupAsync(episodeGroupId, apiKey, setting.Language, setting.RateLimitPerSecond, ct);
        _logger.LogInformation("TMDB 剧集组远端拉取：{Id}，分组数={Groups}", episodeGroupId, fresh.Groups.Count);
        await UpsertSearchCacheAsync(ctx, hash, $"episode_group:{episodeGroupId}", JsonSerializer.Serialize(fresh), ct);
        return fresh;
    }

    /// <summary>容错下载海报到本地缓存（失败仅记 Warning，不影响详情返回）</summary>
    /// <remarks>
    /// 触发点放在元数据查询而非归档：让人工确认页 / 历史详情页在归档前就能展示真实海报。
    /// DownloadAsync 自身按文件存在幂等跳过，故缓存命中路径重复调用开销极小（仅一次 File.Exists）。
    /// 取消异常照常上抛（正常控制流）；其余异常吞掉记 Warning，海报缺失由前端降级到占位图。
    /// </remarks>
    private async Task TryCachePosterAsync(int tmdbId, string? posterPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(posterPath)) return;
        try
        {
            await _poster.DownloadAsync(tmdbId, posterPath, _paths.CacheDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "海报下载失败（不影响 TMDB 详情）：TmdbId={TmdbId}", tmdbId);
        }
    }

    private static async Task<TmdbSetting> LoadSettingAsync(PmmDbContext ctx, CancellationToken ct)
    {
        TmdbSetting? s = await ctx.TmdbSettings.FirstOrDefaultAsync(x => x.Id == TmdbSettingId, ct);
        return s ?? throw new BusinessException("TMDB 配置单例缺失（请检查 Migration 种子）");
    }

    private string DecryptApiKey(TmdbSetting setting)
    {
        if (string.IsNullOrEmpty(setting.ApiKeyEncrypted))
            throw new BusinessException("尚未配置 TMDB ApiKey");
        return _protector.Unprotect(setting.ApiKeyEncrypted);
    }

    private static string ComputeQueryHash(TmdbSearchRequest req)
    {
        string canonical = $"{req.Query}|{req.MediaType.ToLowerInvariant()}|{req.Year?.ToString() ?? ""}|{req.Language}|{req.FallbackLanguage}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>剧集组缓存键：命名空间前缀 episode_group| 隔离，绝不与搜索条目碰撞</summary>
    private static string ComputeEpisodeGroupHash(string episodeGroupId, string language)
    {
        string canonical = $"episode_group|{episodeGroupId}|{language}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // 排错可读文本附语言段：同一 query 现在会按 语言×年份 产生多条缓存行（回退链），便于 DB 直查区分
    private static string ToQueryRaw(TmdbSearchRequest req) =>
        $"{req.MediaType}:{req.Query}"
        + (req.Year is null ? string.Empty : $"({req.Year})")
        + (string.IsNullOrEmpty(req.Language) ? string.Empty : $"[{req.Language}]");

    private static async Task UpsertSearchCacheAsync(PmmDbContext ctx, string hash, string raw, string resultsJson, CancellationToken ct)
    {
        TmdbSearchCache? existing = await ctx.TmdbSearchCaches.FirstOrDefaultAsync(c => c.QueryHash == hash, ct);
        if (existing is null)
        {
            ctx.TmdbSearchCaches.Add(new TmdbSearchCache
            {
                QueryHash = hash,
                QueryRaw = raw,
                Results = resultsJson,
                CachedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.QueryRaw = raw;
            existing.Results = resultsJson;
            existing.CachedAt = DateTimeOffset.UtcNow;
        }
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task UpsertMetadataCacheAsync(PmmDbContext ctx, TmdbDetailsResult details, CancellationToken ct)
    {
        TmdbMetadataCache? existing = await ctx.TmdbMetadataCaches.FirstOrDefaultAsync(
            c => c.TmdbId == details.TmdbId && c.MediaType == details.MediaType, ct);

        if (existing is null)
        {
            ctx.TmdbMetadataCaches.Add(new TmdbMetadataCache
            {
                TmdbId = details.TmdbId,
                MediaType = details.MediaType,
                Title = details.Title,
                OriginalTitle = details.OriginalTitle,
                Year = details.Year,
                TotalSeasons = details.TotalSeasons,
                PosterPath = details.PosterPath,
                OriginCountry = details.OriginCountry?.ToList(),
                OriginalLanguage = details.OriginalLanguage,
                Genres = details.GenresJson,
                Overview = details.Overview,
                RawJson = details.RawJson,
                CachedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Title = details.Title;
            existing.OriginalTitle = details.OriginalTitle;
            existing.Year = details.Year;
            existing.TotalSeasons = details.TotalSeasons;
            existing.PosterPath = details.PosterPath;
            existing.OriginCountry = details.OriginCountry?.ToList();
            existing.OriginalLanguage = details.OriginalLanguage;
            existing.Genres = details.GenresJson;
            existing.Overview = details.Overview;
            existing.RawJson = details.RawJson;
            existing.CachedAt = DateTimeOffset.UtcNow;
        }
        await ctx.SaveChangesAsync(ct);
    }
}
