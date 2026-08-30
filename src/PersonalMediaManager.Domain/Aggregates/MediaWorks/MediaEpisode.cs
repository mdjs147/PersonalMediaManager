namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>剧集单集（Media_Episode）— MediaWork 聚合内子实体，含每集简介</summary>
/// <remarks>UQ (WorkId, SeasonNumber, EpisodeNumber)；由 ReplaceEpisodes 重建；前端按季展开，标注本地文件是否存在。</remarks>
public sealed class MediaEpisode
{
    private MediaEpisode() { }

    internal MediaEpisode(long workId, int seasonNumber, int episodeNumber, string? name, string? overview, string? stillPath, DateTimeOffset? airDate, int? runtime, double? voteAverage)
    {
        WorkId = workId;
        SeasonNumber = seasonNumber;
        EpisodeNumber = episodeNumber;
        Name = name;
        Overview = overview;
        StillPath = stillPath;
        AirDate = airDate;
        Runtime = runtime;
        VoteAverage = voteAverage;
    }

    public long Id { get; private set; }

    /// <summary>所属作品（FK CASCADE）</summary>
    public long WorkId { get; private set; }

    public int SeasonNumber { get; private set; }

    public int EpisodeNumber { get; private set; }

    public string? Name { get; private set; }

    /// <summary>每集简介</summary>
    public string? Overview { get; private set; }

    /// <summary>剧照路径（TMDB still_path）</summary>
    public string? StillPath { get; private set; }

    public DateTimeOffset? AirDate { get; private set; }

    /// <summary>时长（分钟）</summary>
    public int? Runtime { get; private set; }

    public double? VoteAverage { get; private set; }
}
