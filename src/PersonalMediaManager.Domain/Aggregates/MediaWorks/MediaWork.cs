using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Aggregates.MediaWorks;

/// <summary>媒体作品聚合根（Media_Work）— 按 TMDB 作品聚合的库内持久实体 + 富化关联</summary>
/// <remarks>
/// 与 MediaItem（每文件一条处理记录）解耦：MediaWork 一行 = 一部作品（UQ TmdbId+MediaType）。
/// 归档完成时最小 upsert 入库（仅标题/年份，不发网络）；详情打开 / 手动刷新时由 WorkEnrichmentService 拉 TMDB 富化数据，
/// 经 Replace* 原子替换 演职员 / 类型 / 公司 / 电视台 / 关键词 / 季 / 集 子集合（充血聚合，禁止应用层直接 new 子行，参照 ProcessStep）。
/// 维度实体（Media_Person/Genre/Company/Network/Keyword）跨作品共享、不属本聚合，由富化服务按 TMDB id upsert，连接表仅持 id。
/// </remarks>
public sealed class MediaWork : AggregateRoot
{
    /// <summary>EF Core 反射构造</summary>
    private MediaWork() { }

    private readonly List<MediaWorkCredit> _credits = new();
    private readonly List<MediaWorkGenre> _genres = new();
    private readonly List<MediaWorkCompany> _companies = new();
    private readonly List<MediaWorkNetwork> _networks = new();
    private readonly List<MediaWorkKeyword> _keywords = new();
    private readonly List<MediaSeason> _seasons = new();
    private readonly List<MediaEpisode> _episodes = new();

    public IReadOnlyCollection<MediaWorkCredit> Credits => _credits.AsReadOnly();
    public IReadOnlyCollection<MediaWorkGenre> Genres => _genres.AsReadOnly();
    public IReadOnlyCollection<MediaWorkCompany> Companies => _companies.AsReadOnly();
    public IReadOnlyCollection<MediaWorkNetwork> Networks => _networks.AsReadOnly();
    public IReadOnlyCollection<MediaWorkKeyword> Keywords => _keywords.AsReadOnly();
    public IReadOnlyCollection<MediaSeason> Seasons => _seasons.AsReadOnly();
    public IReadOnlyCollection<MediaEpisode> Episodes => _episodes.AsReadOnly();

    /// <summary>归档完成最小入库工厂（仅 TmdbId/MediaType/Title/Year，不发网络；关联留待惰性富化）</summary>
    public static MediaWork CreateMinimal(int tmdbId, string mediaType, string? title, int? year)
        => new()
        {
            TmdbId = tmdbId,
            MediaType = mediaType.ToLowerInvariant(),
            Title = title,
            Year = year,
        };

    // ---------- 作品级标量 ----------

    /// <summary>TMDB 资源 ID</summary>
    public int TmdbId { get; private set; }

    /// <summary>movie / tv（小写 TMDB 原值）</summary>
    public string MediaType { get; private set; } = default!;

    public string? Title { get; private set; }
    public string? OriginalTitle { get; private set; }
    public int? Year { get; private set; }
    public string? Overview { get; private set; }
    public string? Tagline { get; private set; }
    public string? PosterPath { get; private set; }
    public string? BackdropPath { get; private set; }

    /// <summary>时长（分钟，电影 runtime / 剧集单集时长）</summary>
    public int? Runtime { get; private set; }

    public double? VoteAverage { get; private set; }
    public int? VoteCount { get; private set; }

    /// <summary>电影上映 / 剧集首播日期</summary>
    public DateTimeOffset? ReleaseDate { get; private set; }

    /// <summary>TMDB 状态（Released / Ended / Returning Series…）</summary>
    public string? TmdbStatus { get; private set; }

    /// <summary>ISO 639-1 原始语言</summary>
    public string? OriginalLanguage { get; private set; }

    /// <summary>JSON 数组 ["CN","US"]（复用 JsonValueConverter）</summary>
    public List<string>? OriginCountry { get; private set; }

    public string? Homepage { get; private set; }
    public int? TotalSeasons { get; private set; }
    public int? TotalEpisodes { get; private set; }

    /// <summary>所属分类（最近归档代表行的 CategoryId 同步而来，可空）</summary>
    public long? CategoryId { get; private set; }

    /// <summary>关联最后富化时间（null = 仅最小入库未富化；做 TTL / 重刷判定）</summary>
    public DateTimeOffset? EnrichedAt { get; private set; }

    // ---------- 标量维护 ----------

    /// <summary>更新作品级标量元数据（富化时调用，不动子集合与 CategoryId）</summary>
    public void UpsertScalars(
        string? title, string? originalTitle, int? year, string? overview, string? tagline,
        string? posterPath, string? backdropPath, int? runtime, double? voteAverage, int? voteCount,
        DateTimeOffset? releaseDate, string? tmdbStatus, string? originalLanguage, List<string>? originCountry,
        string? homepage, int? totalSeasons, int? totalEpisodes)
    {
        Title = title;
        OriginalTitle = originalTitle;
        Year = year;
        Overview = overview;
        Tagline = tagline;
        PosterPath = posterPath;
        BackdropPath = backdropPath;
        Runtime = runtime;
        VoteAverage = voteAverage;
        VoteCount = voteCount;
        ReleaseDate = releaseDate;
        TmdbStatus = tmdbStatus;
        OriginalLanguage = originalLanguage;
        OriginCountry = originCountry;
        Homepage = homepage;
        TotalSeasons = totalSeasons;
        TotalEpisodes = totalEpisodes;
    }

    /// <summary>同步分类（取最近归档代表行 CategoryId）</summary>
    public void SetCategory(long? categoryId) => CategoryId = categoryId;

    /// <summary>标记富化完成时刻</summary>
    public void MarkEnriched(DateTimeOffset at) => EnrichedAt = at;

    // ---------- 子集合原子替换（清空 + 重建，EF 级联删旧增新） ----------

    public void ReplaceCredits(IEnumerable<CreditSeed> seeds)
    {
        _credits.Clear();
        foreach (CreditSeed s in seeds)
            _credits.Add(new MediaWorkCredit(Id, s.PersonId, s.CreditType, s.Character, s.Ord, s.Job, s.Department));
    }

    public void ReplaceGenres(IEnumerable<int> genreIds)
    {
        _genres.Clear();
        foreach (int gid in genreIds.Distinct())
            _genres.Add(new MediaWorkGenre(Id, gid));
    }

    public void ReplaceCompanies(IEnumerable<int> companyIds)
    {
        _companies.Clear();
        foreach (int cid in companyIds.Distinct())
            _companies.Add(new MediaWorkCompany(Id, cid));
    }

    public void ReplaceNetworks(IEnumerable<int> networkIds)
    {
        _networks.Clear();
        foreach (int nid in networkIds.Distinct())
            _networks.Add(new MediaWorkNetwork(Id, nid));
    }

    public void ReplaceKeywords(IEnumerable<int> keywordIds)
    {
        _keywords.Clear();
        foreach (int kid in keywordIds.Distinct())
            _keywords.Add(new MediaWorkKeyword(Id, kid));
    }

    public void ReplaceSeasons(IEnumerable<SeasonSeed> seeds)
    {
        _seasons.Clear();
        foreach (SeasonSeed s in seeds)
            _seasons.Add(new MediaSeason(Id, s.SeasonNumber, s.Name, s.Overview, s.PosterPath, s.AirDate, s.EpisodeCount));
    }

    /// <summary>替换某一季的全部分集（惰性按季富化：用户展开某季时调，仅动该季的 _episodes）</summary>
    /// <remarks>调用前需 Include 该季已有 Episodes（或全集合）使 EF 能级联删旧行；只删本季、增本季。</remarks>
    public void ReplaceSeasonEpisodes(int seasonNumber, IEnumerable<EpisodeSeed> seeds)
    {
        _episodes.RemoveAll(e => e.SeasonNumber == seasonNumber);
        foreach (EpisodeSeed s in seeds)
            _episodes.Add(new MediaEpisode(Id, s.SeasonNumber, s.EpisodeNumber, s.Name, s.Overview, s.StillPath, s.AirDate, s.Runtime, s.VoteAverage));
    }

    /// <summary>清空全部分集缓存（force 重刷时调，使各季下次展开重新拉取）</summary>
    /// <remarks>调用前需 Include 全部 Episodes 使 EF 能级联删除。</remarks>
    public void ClearEpisodes() => _episodes.Clear();
}
