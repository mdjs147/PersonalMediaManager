using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Services.Tmdb;

/// <summary>TMDB 搜索 + 元数据获取的应用编排（D2.2）</summary>
/// <remarks>
/// 职责：读 Tmdb_Setting 拿 ApiKey + 缓存层（Tmdb_SearchCache 1h、Tmdb_MetadataCache 24h）+ 委托 ITmdbClient。
/// 调用方（D7 ProcessFileService）只问 Search/Details，不关心缓存命中与底层 HTTP。
/// 缓存命中时不再走 TMDB，节流额度也不消耗。
/// </remarks>
public interface ITmdbSearchService
{
    Task<TmdbSearchResult> SearchAsync(TmdbSearchRequest request, CancellationToken ct = default);

    Task<TmdbDetailsResult> GetDetailsAsync(int tmdbId, string mediaType, CancellationToken ct = default);

    /// <summary>详情按 TTL 或强制刷新，失败保留旧缓存并返回错误</summary>
    /// <remarks>默认实现转发旧接口，以兼容既有实现；生产服务支持 forceRefresh。</remarks>
    Task<TmdbDetailsResult> GetDetailsFreshAsync(int tmdbId, string mediaType,
        bool forceRefresh = false, CancellationToken ct = default)
        => GetDetailsAsync(tmdbId, mediaType, ct);

    /// <summary>读取指定 TV 的单季目录，支持强制刷新与失败回退</summary>
    Task<TmdbSeasonCatalogueResult> GetSeasonCatalogueAsync(int tmdbId, int seasonNumber,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new TmdbSeasonCatalogueResult(tmdbId, "tv", null,
            RefreshError: "当前服务未提供季集目录"));
    }


    /// <summary>读剧集组（带本地缓存）：强制匹配标识携带剧集组 id 时，用于"编组内集号 → 正典季集"翻译</summary>
    Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(string episodeGroupId, CancellationToken ct = default);
}
