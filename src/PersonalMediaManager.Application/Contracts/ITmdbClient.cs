namespace PersonalMediaManager.Application.Contracts;

/// <summary>TMDB v3/v4 客户端契约（D2.1）</summary>
/// <remarks>
/// 实现在 Infrastructure.External/Tmdb/TmdbClient.cs：IHttpClientFactory + 令牌桶限流（默认 40 req/s）
/// + 429 Retry-After 退避（最多 3 次重试；等待超过 60s 视为不可恢复直接抛异常）。
/// ApiKey 从调用方（TmdbSearchService）每次显式传入，避免 External 反向依赖 Persistence 读 Tmdb_Setting。
/// 限流速率同理由调用方传入（rateLimitPerSecond，对应 Tmdb_Setting.RateLimitPerSecond）：
/// 传 null 表示沿用当前速率；速率在客户端单例上具有粘性，SearchAsync / GetDetailsAsync 应用后，
/// GetEnrichedDetailsAsync / GetSeasonAsync 复用最近一次生效的速率（这两个方法签名保持不变）。
/// 失败语义：网络异常 / 4xx / 5xx 一律抛 TmdbClientException（含 HttpStatus + Message），调用方决定降级。
/// </remarks>
public interface ITmdbClient
{
    /// <summary>搜索 TMDB（movie / tv 二选一）</summary>
    Task<TmdbSearchResult> SearchAsync(
        TmdbSearchRequest request,
        string apiKey,
        int? rateLimitPerSecond = null,
        CancellationToken ct = default);

    /// <summary>读详情（D2.2 SearchService 在用户确认或归档前调；含原始 RawJson 入元数据缓存）</summary>
    Task<TmdbDetailsResult> GetDetailsAsync(
        int tmdbId,
        string mediaType,
        string apiKey,
        string language = "zh-CN",
        int? rateLimitPerSecond = null,
        CancellationToken ct = default);

    /// <summary>读富化详情（append_to_response=credits,keywords）：媒体库写 Media_Work + 演职员/公司/类型/关键词关联用</summary>
    /// <remarks>与 GetDetailsAsync 解耦：解析层不同（含 cast/crew/companies/networks/keywords/季摘要），不污染既有 TmdbMetadataCache 流程。</remarks>
    Task<TmdbEnrichedDetails> GetEnrichedDetailsAsync(
        int tmdbId,
        string mediaType,
        string apiKey,
        string language = "zh-CN",
        CancellationToken ct = default);

    /// <summary>读剧集单季分集（/tv/{id}/season/{n}）：每集简介/剧照/时长用</summary>
    Task<TmdbSeasonDetail> GetSeasonAsync(
        int tmdbId,
        int seasonNumber,
        string apiKey,
        string language = "zh-CN",
        CancellationToken ct = default);

    /// <summary>读剧集组（/tv/episode_group/{id}）：把"重制版/特殊编组的集号"翻译回正典季集</summary>
    /// <remarks>
    /// 剧集组（episode_group）是 TMDB 对正典分集的重新编组/排序（HD Remaster、DVD 序、故事线序等）。
    /// 强制匹配标识（pmm.txt）携带剧集组 id 时，用本接口把文件名里的"编组内集号"映射到正典 season/episode。
    /// </remarks>
    Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(
        string episodeGroupId,
        string apiKey,
        string language = "zh-CN",
        int? rateLimitPerSecond = null,
        CancellationToken ct = default);
}

/// <summary>搜索入参</summary>
/// <remarks>
/// Language / FallbackLanguage 缺省为 null（= 未显式指定）：
/// 经 TmdbSearchService 编排时由 Tmdb_Setting.Language / FallbackLanguage 注入（修复「语言死旋钮」——
/// 旧版 record 默认 "zh-CN" 使配置永远不生效）；直连 TmdbClient 且为 null 时按 zh-CN 兜底，行为与旧版一致。
/// </remarks>
public sealed record TmdbSearchRequest(
    string Query,
    string MediaType,         // "movie" | "tv" | "unknown"（unknown 时实现两个都查并合并）
    int? Year = null,
    string? Language = null,
    string? FallbackLanguage = null);

/// <summary>搜索结果（候选列表）</summary>
/// <remarks>FromCache：true=命中本地搜索缓存(Tmdb_SearchCache，不计远端额度)；false=远端拉取。供时间线/日志标注数据来源。</remarks>
public sealed record TmdbSearchResult(
    IReadOnlyList<TmdbCandidate> Candidates,
    string? RawJson,          // 整段 TMDB 响应，用于写 Tmdb_SearchCache
    bool FromCache = false);

public sealed record TmdbCandidate(
    int Id,
    string MediaType,         // "movie" | "tv"
    string? Title,            // 中文标题（language=zh-CN 优先）
    string? OriginalTitle,    // 原始语言标题
    int? Year,
    double Popularity,
    string? OriginalLanguage, // ISO 639-1
    IReadOnlyList<string>? OriginCountry,
    string? PosterPath,
    string? Overview);

/// <summary>详情结果（用于写 Tmdb_MetadataCache）</summary>
/// <remarks>Seasons 为剧集逐季集数（来自 seasons[]，电影为 null）；可选默认值避免破坏既有 positional 构造。</remarks>
public sealed record TmdbDetailsResult(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    int? TotalSeasons,
    string? PosterPath,
    IReadOnlyList<string>? OriginCountry,
    string? OriginalLanguage,
    string? GenresJson,       // 原始 [{id,name}] JSON
    string? Overview,
    string RawJson,
    IReadOnlyList<TmdbSeasonInfo>? Seasons = null,
    bool FromCache = false);   // true=命中本地元数据缓存(Tmdb_MetadataCache)；false=远端拉取

/// <summary>剧集单季集数 + 季名 + 季首播年（绝对集号换算 + 篇章对照 + 归档季文件夹/季年份用）</summary>
/// <remarks>SeasonNumber=0 为特别篇；EpisodeCount 取自 TMDB seasons[].episode_count，未播季可能为 0；
/// Name 取自 seasons[].name（language=zh-CN 优先），供审核页与篇章标题（如「锻刀村篇」）人工对照选季；
/// Year 取自 seasons[].air_date 的年份（未播季 / 无 air_date 为 null），供归档时季内文件按该季首播年命名（而非整剧首播年）。</remarks>
public sealed record TmdbSeasonInfo(int SeasonNumber, int EpisodeCount, string? Name = null, int? Year = null);

// ---------- 富化详情（媒体库 Media_Work 用，与上方基础详情解耦） ----------

/// <summary>富化详情结果（写 Media_Work + 各关联表）</summary>
public sealed record TmdbEnrichedDetails(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? Tagline,
    string? PosterPath,
    string? BackdropPath,
    int? Runtime,
    double? VoteAverage,
    int? VoteCount,
    DateTimeOffset? ReleaseDate,
    string? TmdbStatus,
    string? OriginalLanguage,
    IReadOnlyList<string>? OriginCountry,
    string? Homepage,
    int? TotalSeasons,
    int? TotalEpisodes,
    IReadOnlyList<TmdbGenreRef> Genres,
    IReadOnlyList<TmdbCompanyRef> Companies,
    IReadOnlyList<TmdbNetworkRef> Networks,
    IReadOnlyList<TmdbKeywordRef> Keywords,
    IReadOnlyList<TmdbCreditRef> Cast,
    IReadOnlyList<TmdbCreditRef> Crew,
    IReadOnlyList<TmdbSeasonSummary> Seasons,
    string RawJson);

public sealed record TmdbGenreRef(int Id, string Name);

public sealed record TmdbCompanyRef(int Id, string Name, string? LogoPath, string? OriginCountry);

public sealed record TmdbNetworkRef(int Id, string Name, string? LogoPath, string? OriginCountry);

public sealed record TmdbKeywordRef(int Id, string Name);

/// <summary>演职员条目（CreditType=cast 用 Character/Order；crew 用 Job/Department）</summary>
public sealed record TmdbCreditRef(
    int PersonId,
    string Name,
    string? ProfilePath,
    string? KnownForDepartment,
    string? Character,
    int? Order,
    string? Job,
    string? Department);

/// <summary>季摘要（来自详情 seasons[]，无需额外请求）</summary>
public sealed record TmdbSeasonSummary(
    int SeasonNumber,
    string? Name,
    string? Overview,
    string? PosterPath,
    DateTimeOffset? AirDate,
    int EpisodeCount);

/// <summary>单季分集详情（来自 /tv/{id}/season/{n}）</summary>
public sealed record TmdbSeasonDetail(
    int SeasonNumber,
    string? Name,
    string? Overview,
    string? PosterPath,
    DateTimeOffset? AirDate,
    IReadOnlyList<TmdbEpisodeRef> Episodes);

public sealed record TmdbEpisodeRef(
    int EpisodeNumber,
    string? Name,
    string? Overview,
    string? StillPath,
    DateTimeOffset? AirDate,
    int? Runtime,
    double? VoteAverage);

// ---------- 剧集组（episode_group）：重制版/特殊编组 → 正典季集翻译 ----------

/// <summary>剧集组详情（/tv/episode_group/{id}）</summary>
/// <remarks>
/// Type 为 TMDB 编组类型枚举：1=原播序 2=绝对序 3=DVD 4=数字 5=故事线 6=制作序 7=电视序。
/// Groups 为编组内的"分组"（语义近似"季"），每个分组按 Order 排列、含一串映射回正典季集的条目。
/// 整个 record 可直接 JSON 序列化进搜索缓存（与候选列表同套路），命中缓存时反序列化还原。
/// </remarks>
public sealed record TmdbEpisodeGroup(
    string Id,
    string? Name,
    int Type,
    IReadOnlyList<TmdbEpisodeGroupSegment> Groups);

/// <summary>剧集组下的一个分组（语义近似"季"；多分组时由 pmm.txt 的 group= 指定）</summary>
public sealed record TmdbEpisodeGroupSegment(
    string Id,
    string? Name,
    int Order,
    IReadOnlyList<TmdbEpisodeGroupEntry> Episodes);

/// <summary>分组内单条目：Order=编组内位置（0 起），SeasonNumber/EpisodeNumber=其对应的正典季集</summary>
public sealed record TmdbEpisodeGroupEntry(
    int Order,
    int SeasonNumber,
    int EpisodeNumber,
    string? Name,
    int? TmdbEpisodeId);

/// <summary>TMDB 客户端异常</summary>
public sealed class TmdbClientException : Exception
{
    public TmdbClientException(string message, int? httpStatus = null, Exception? inner = null) : base(message, inner)
    {
        HttpStatus = httpStatus;
    }

    public int? HttpStatus { get; }
}
