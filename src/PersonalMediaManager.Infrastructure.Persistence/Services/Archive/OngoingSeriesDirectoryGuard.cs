using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

/// <summary>只采纳有效 TMDB 整剧详情中的明确未完结证据</summary>
/// <remarks>不以本季集数、文件数量或目录是否为空推断整剧完结；未知、过期、矛盾数据不触发额外保留。</remarks>
internal static class OngoingSeriesDirectoryGuard
{
    internal const string SettingKey = "File.CleanEmptyDirKeepOngoingSeries";

    internal const int CandidatePageSize = 128;

    /// <summary>在指定子树内分批查找有效且明确未完结的剧集</summary>
    /// <remarks>按主键游标分页，候选和缓存每批最多 128 行；不物化整个媒体历史，也不截断后续匹配。</remarks>
    internal static async Task<TmdbMetadataCache?> FindForDirectoryAsync(
        PmmDbContext db, SourceDirectoryCleanupScope scope, MediaItem currentItem, DateTimeOffset now, CancellationToken ct)
    {
        int? cacheHours = await db.TmdbSettings.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => (int?)s.MetadataCacheHours).FirstOrDefaultAsync(ct);
        if (cacheHours is null) return null;
        // 当前归档结果可能尚未提交；其新身份优先于历史数据库行。
        if (string.Equals(currentItem.TmdbMediaType, "tv", StringComparison.OrdinalIgnoreCase)
            && currentItem.TmdbId is int currentId && scope.Contains(currentItem.SourcePath))
        {
            TmdbMetadataCache? currentCache = await db.TmdbMetadataCaches.AsNoTracking()
                .FirstOrDefaultAsync(m => m.MediaType == "tv" && m.TmdbId == currentId, ct);
            if (IsConfirmedOngoing(currentCache, now, cacheHours.Value)) return currentCache;
        }

        long afterId = long.MinValue;
        while (true)
        {
            // LIKE 仅为路径超集；Windows 的 Unicode 大小写由 Contains 在宽筛后准确裁定。
            List<SourceCandidate> page = await db.MediaItems.AsNoTracking()
                .Where(m => m.Id > afterId && m.Id != currentItem.Id && m.TmdbId != null && m.TmdbMediaType != null
                    && m.TmdbMediaType.ToLower() == "tv" && EF.Functions.Like(m.SourcePath, scope.LikePattern, "!"))
                .OrderBy(m => m.Id)
                .Select(m => new SourceCandidate(m.Id, m.SourcePath, m.TmdbId!.Value))
                .Take(CandidatePageSize).ToListAsync(ct);
            int[] ids = page.Where(m => scope.Contains(m.SourcePath)).Select(m => m.TmdbId).Distinct().ToArray();
            if (ids.Length > 0)
            {
                List<TmdbMetadataCache> caches = await db.TmdbMetadataCaches.AsNoTracking()
                    .Where(m => m.MediaType == "tv" && ids.Contains(m.TmdbId)).ToListAsync(ct);
                TmdbMetadataCache? ongoing = caches.FirstOrDefault(m => IsConfirmedOngoing(m, now, cacheHours.Value));
                if (ongoing is not null) return ongoing;
            }
            if (page.Count < CandidatePageSize) return null;
            afterId = page[^1].Id;
        }
    }

    private sealed record SourceCandidate(long Id, string SourcePath, int TmdbId);

    internal static bool IsConfirmedOngoing(TmdbMetadataCache? cache, DateTimeOffset now, int cacheHours)
    {
        if (cache is null || cache.TmdbId <= 0 || !string.Equals(cache.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            || cacheHours <= 0 || cache.CachedAt == default || cache.CachedAt > now
            || (now - cache.CachedAt).TotalHours > cacheHours
            || string.IsNullOrWhiteSpace(cache.RawJson)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(cache.RawJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // 原始详情存在身份字段时必须一致，避免错误关联的缓存成为肯定证据。
            if (root.TryGetProperty("id", out JsonElement id)
                && (!id.TryGetInt32(out int rawId) || rawId != cache.TmdbId)) return false;
            bool hasStatus = root.TryGetProperty("status", out JsonElement statusValue);
            bool hasProduction = root.TryGetProperty("in_production", out JsonElement production);
            if (hasStatus && statusValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            if (hasProduction && production.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return false;
            string? status = hasStatus && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString()?.Trim() : null;
            bool? inProduction = hasProduction && production.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? production.GetBoolean() : null;
            if (string.Equals(status, "Ended", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)) return false;
            if (inProduction == false) return false;
            bool explicitlyOngoing = string.Equals(status, "Returning Series", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "In Production", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Planned", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Pilot", StringComparison.OrdinalIgnoreCase);
            return explicitlyOngoing || (string.IsNullOrEmpty(status) && inProduction == true);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
