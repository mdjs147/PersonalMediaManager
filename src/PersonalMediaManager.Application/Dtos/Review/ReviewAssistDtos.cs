using System.ComponentModel.DataAnnotations;

namespace PersonalMediaManager.Application.Dtos.Review;

/// <summary>只读重新提取季集，不调用模型、不保存</summary>
public sealed record ReviewEpisodeHintsRequest(
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<ReviewEpisodeHintItem> Items);
public sealed record ReviewEpisodeHintItem(long Id, long RowVersion);
public sealed record ReviewEpisodeHintsResult(IReadOnlyList<ReviewEpisodeHint> Items);
public sealed record ReviewEpisodeHint(long Id, int? Season, int? Episode, int? EpisodeEnd,
    string Source, IReadOnlyList<string> Evidence, string? Error);

/// <summary>库内推荐是待人工选择的候选</summary>
public sealed record ReviewLibraryCandidatesResult(IReadOnlyList<ReviewLibraryCandidate> Items);
public sealed record ReviewLibraryCandidate(int TmdbId, string MediaType, string? Title,
    string? OriginalTitle, int? Year, string? PosterUrl, int? TotalSeasons,
    string Source, string MatchReason, string? TmdbStatus, DateTimeOffset? LatestArchivedAt);

/// <summary>用户声明累计编号后，只读计算目标季的映射预览</summary>
public sealed record ReviewEpisodeMappingRequest(
    int TmdbId, string MediaType, [Range(2, 100)] int Season,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<ReviewEpisodeMappingItem> Items,
    bool ForceRefresh = false);
public sealed record ReviewEpisodeMappingItem(long Id, long RowVersion, int Episode, int? EpisodeEnd = null);
public sealed record ReviewEpisodeMappingResult(IReadOnlyList<ReviewEpisodeMappingEntry> Items,
    DateTimeOffset? CachedAt, string? RefreshError);
public sealed record ReviewEpisodeMappingEntry(long Id,
    int? OriginalSeason, int OriginalEpisode, int? OriginalEpisodeEnd,
    int? Season, int? Episode, int? EpisodeEnd, string Source,
    IReadOnlyList<string> Evidence, string? Error, string? MappingToken = null);
