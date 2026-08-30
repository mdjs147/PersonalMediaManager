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

    /// <summary>读剧集组（带本地缓存）：强制匹配标识携带剧集组 id 时，用于"编组内集号 → 正典季集"翻译</summary>
    Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(string episodeGroupId, CancellationToken ct = default);
}
