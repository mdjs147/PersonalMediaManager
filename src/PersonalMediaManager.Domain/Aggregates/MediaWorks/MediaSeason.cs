namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>剧集季摘要（Media_Season）— MediaWork 聚合内子实体</summary>
/// <remarks>UQ (WorkId, SeasonNumber)；由 ReplaceSeasons 重建；仅剧集有。SeasonNumber=0 为特别篇。</remarks>
public sealed class MediaSeason
{
    private MediaSeason() { }

    internal MediaSeason(long workId, int seasonNumber, string? name, string? overview, string? posterPath, DateTimeOffset? airDate, int episodeCount)
    {
        WorkId = workId;
        SeasonNumber = seasonNumber;
        Name = name;
        Overview = overview;
        PosterPath = posterPath;
        AirDate = airDate;
        EpisodeCount = episodeCount;
    }

    public long Id { get; private set; }

    /// <summary>所属作品（FK CASCADE）</summary>
    public long WorkId { get; private set; }

    public int SeasonNumber { get; private set; }

    public string? Name { get; private set; }

    public string? Overview { get; private set; }

    /// <summary>季海报路径（TMDB poster_path）</summary>
    public string? PosterPath { get; private set; }

    public DateTimeOffset? AirDate { get; private set; }

    /// <summary>该季集数（TMDB episode_count）</summary>
    public int EpisodeCount { get; private set; }
}
