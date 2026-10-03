using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

internal sealed partial class TmdbSearchService
{
    public Task<TmdbDetailsResult> GetDetailsFreshAsync(int tmdbId, string mediaType,
        bool forceRefresh = false, CancellationToken ct = default)
        => GetDetailsCoreAsync(tmdbId, mediaType, ct, forceRefresh, allowStaleOnError: true);

    public async Task<TmdbSeasonCatalogueResult> GetSeasonCatalogueAsync(int tmdbId, int seasonNumber,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        NormalizeDetailsIdentity(tmdbId, "tv");
        if (seasonNumber < 0) throw new BusinessException("季号不能小于零");
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting setting = await LoadSettingAsync(ctx, ct);
        // 独立命名空间并固定 TV 身份；同数字电影、不同季与不同语言绝不复用目录。
        string canonical = $"season_catalogue|tv|{tmdbId}|{seasonNumber}|{setting.Language}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        TmdbSearchCache? cached = await ctx.TmdbSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.QueryHash == hash, ct);
        TmdbSeasonCatalogueResult? hit = RestoreSeasonCatalogue(cached, tmdbId, seasonNumber);
        if (!forceRefresh && hit is not null && cached!.CachedAt >= DateTimeOffset.UtcNow.AddHours(-setting.MetadataCacheHours))
        {
            _logger.LogInformation("TMDB 季目录命中缓存：tmdbId={TmdbId}, season={Season}", tmdbId, seasonNumber);
            ct.ThrowIfCancellationRequested();
            return hit;
        }
        try
        {
            string apiKey = DecryptApiKey(setting);
            TmdbSeasonDetail season = await _client.GetSeasonAsync(tmdbId, seasonNumber, apiKey,
                setting.Language, setting.RateLimitPerSecond, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsValidSeason(season, seasonNumber))
                throw new TmdbClientException("TMDB 返回的季集目录身份或集号不一致");
            TmdbSeasonCatalogueResult fresh = new(tmdbId, "tv", season, CacheTimestampUtcNow());
            await UpsertSearchCacheAsync(ctx, hash, $"season_catalogue:tv:{tmdbId}:{seasonNumber}[{setting.Language}]",
                JsonSerializer.Serialize(fresh), ct, fresh.CachedAt);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("TMDB 季目录已刷新：tmdbId={TmdbId}, season={Season}", tmdbId, seasonNumber);
            return fresh;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            string error = SafeRefreshError(ex);
            _logger.LogWarning("TMDB 季目录刷新失败，保留原缓存：tmdbId={TmdbId}, season={Season}, error={Error}", tmdbId, seasonNumber, error);
            ParseDiagnostics.Emit("tmdb.season_refresh_failed", new { tmdbId, seasonNumber, error = ParseDiagnostics.CaptureText(ex.Message) });
            return hit is not null ? hit with { RefreshError = error }
                : new TmdbSeasonCatalogueResult(tmdbId, "tv", null, RefreshError: error);
        }
    }

    private static string NormalizeDetailsIdentity(int tmdbId, string mediaType)
    {
        if (tmdbId <= 0) throw new BusinessException("TMDB 编号必须大于零");
        string normType = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normType is not ("tv" or "movie")) throw new BusinessException("媒体类型必须是 movie 或 tv");
        return normType;
    }

    private static TmdbDetailsResult? RestoreDetails(TmdbMetadataCache? cached)
    {
        if (cached?.RawJson is null) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(cached.RawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            TmdbDetailsResult restored = new(cached.TmdbId, cached.MediaType, cached.Title, cached.OriginalTitle,
                cached.Year, cached.TotalSeasons, cached.PosterPath, cached.OriginCountry, cached.OriginalLanguage,
                cached.Genres, cached.Overview, cached.RawJson, TmdbSeasonsParser.Parse(cached.RawJson),
                FromCache: true, CachedAt: cached.CachedAt);
            ValidateDetailsIdentity(restored, cached.TmdbId, cached.MediaType);
            return restored;
        }
        catch (Exception ex) when (ex is JsonException or TmdbClientException or InvalidOperationException) { return null; }
    }

    // SQLite 日期转换器会截断子毫秒精度；回传时间与后续缓存命中保持一致。
    private static DateTimeOffset CacheTimestampUtcNow()
        => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static string SafeRefreshError(Exception ex) => ex switch
    {
        TmdbClientException { HttpStatus: 429 } => "TMDB 请求限流（429），请稍后重试",
        TmdbClientException { HttpStatus: int status } when status is >= 100 and <= 599
            => $"TMDB 服务返回错误（{status}），请稍后重试",
        HttpRequestException => "TMDB 网络连接失败，请稍后重试",
        JsonException => "TMDB 返回的数据格式不正确，请稍后重试",
        _ => "TMDB 元数据刷新失败，请稍后重试",
    };

    private static void ValidateDetailsIdentity(TmdbDetailsResult details, int tmdbId, string mediaType)
    {
        if (details is null || details.TmdbId != tmdbId
            || !string.Equals(details.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            throw new TmdbClientException("TMDB 返回的作品身份与请求不一致");
        if (string.IsNullOrWhiteSpace(details.Title) && string.IsNullOrWhiteSpace(details.OriginalTitle))
            throw new TmdbClientException("TMDB 返回的详情缺少作品名称，保留已有资料");
        // 错误页与损坏响应不能写回、顶替已有的有效 JSON。
        using JsonDocument doc = JsonDocument.Parse(details.RawJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || (doc.RootElement.TryGetProperty("id", out JsonElement id)
                && (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int responseId) || responseId != tmdbId)))
            throw new TmdbClientException("TMDB 返回的详情身份或格式不正确");
    }

    private static TmdbSeasonCatalogueResult? RestoreSeasonCatalogue(TmdbSearchCache? cached, int tmdbId, int seasonNumber)
    {
        if (cached is null) return null;
        try
        {
            TmdbSeasonCatalogueResult? restored = JsonSerializer.Deserialize<TmdbSeasonCatalogueResult>(cached.Results);
            if (restored?.TmdbId != tmdbId || restored.MediaType != "tv" || !IsValidSeason(restored.Season, seasonNumber))
                return null;
            return restored with { FromCache = true, CachedAt = cached.CachedAt, RefreshError = null };
        }
        catch (JsonException) { return null; }
    }

    private static bool IsValidSeason(TmdbSeasonDetail? season, int seasonNumber)
        => season is not null && season.SeasonNumber == seasonNumber && season.Episodes is not null
            && season.Episodes.All(e => e is not null && e.EpisodeNumber > 0)
            && season.Episodes.Select(e => e.EpisodeNumber).Distinct().Count() == season.Episodes.Count;
}
