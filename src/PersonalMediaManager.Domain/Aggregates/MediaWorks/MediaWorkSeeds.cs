namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>富化时向 MediaWork.ReplaceCredits 传入的演职员种子</summary>
/// <remarks>由富化服务（Persistence）从 TMDB 解析结果填充；Domain 据此构造 MediaWorkCredit，保持聚合纪律。</remarks>
public readonly record struct CreditSeed(
    int PersonId,
    string CreditType,
    string? Character,
    int? Ord,
    string? Job,
    string? Department);

/// <summary>向 MediaWork.ReplaceSeasons 传入的季种子</summary>
public readonly record struct SeasonSeed(
    int SeasonNumber,
    string? Name,
    string? Overview,
    string? PosterPath,
    DateTimeOffset? AirDate,
    int EpisodeCount);

/// <summary>向 MediaWork.ReplaceEpisodes 传入的集种子</summary>
public readonly record struct EpisodeSeed(
    int SeasonNumber,
    int EpisodeNumber,
    string? Name,
    string? Overview,
    string? StillPath,
    DateTimeOffset? AirDate,
    int? Runtime,
    double? VoteAverage);
