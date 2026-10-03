namespace PersonalMediaManager.Application.Contracts;

/// <summary>带身份与刷新状态的单季目录</summary>
/// <remarks>Season 为远端原始目录；失败且没有有效旧缓存时为空，不能用库内补全内容冒充新目录。</remarks>
public sealed record TmdbSeasonCatalogueResult(
    int TmdbId,
    string MediaType,
    TmdbSeasonDetail? Season,
    DateTimeOffset? CachedAt = null,
    bool FromCache = false,
    string? RefreshError = null);
