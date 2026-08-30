using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Library;

/// <summary>媒体库查询参数（GET /library）</summary>
/// <param name="Type">类型过滤：movie / tv；空 = 全部</param>
/// <param name="CategoryId">分类过滤</param>
/// <param name="Keyword">标题模糊匹配</param>
/// <param name="GenreId">类型(genre) 过滤（TMDB genre id）</param>
/// <param name="PersonId">演职员过滤（TMDB person id，同时命中出演与制作）</param>
/// <param name="CompanyId">制作公司过滤（TMDB company id）</param>
/// <param name="NetworkId">电视台过滤（TMDB network id）</param>
/// <param name="KeywordId">关键词/标签过滤（TMDB keyword id）</param>
/// <param name="Country">出品国家过滤（ISO 3166-1，如 CN）</param>
/// <param name="Language">原始语言过滤（ISO 639-1，如 zh）</param>
/// <param name="YearFrom">年份下限（含）</param>
/// <param name="YearTo">年份上限（含）</param>
/// <param name="Sort">排序：recent（默认，最近归档）/ year / rating / title</param>
/// <param name="Page">页码（≥ 1）</param>
/// <param name="PageSize">每页（上限 100，默认 24）</param>
public sealed record LibraryListQuery(
    string? Type = null,
    long? CategoryId = null,
    string? Keyword = null,
    int? GenreId = null,
    int? PersonId = null,
    int? CompanyId = null,
    int? NetworkId = null,
    int? KeywordId = null,
    string? Country = null,
    string? Language = null,
    int? YearFrom = null,
    int? YearTo = null,
    string? Sort = null,
    int Page = 1,
    int PageSize = 24);

/// <summary>媒体库卡片（按作品聚合，剧集多集归一卡）</summary>
/// <param name="TmdbId">TMDB 主键（同时用于拼海报端点 /library/poster/{tmdbId}）</param>
/// <param name="MediaType">movie / tv</param>
/// <param name="Title">展示标题（Media_Work 优先 → 元数据缓存 → 解析标题）</param>
/// <param name="Year">年份</param>
/// <param name="Rating">TMDB 评分（富化后有值，0~10）</param>
/// <param name="CategoryName">所属分类名（可空）</param>
/// <param name="FileCount">该作品已归档文件数（电影通常 1，剧集 = 集数）</param>
/// <param name="MissingFileCount">已检测为缺失的文件数（整库存在性扫描后有值）</param>
/// <param name="LatestArchivedAt">最近一次归档时间</param>
/// <param name="HasPoster">本地是否已缓存海报</param>
/// <param name="Genres">类型名（最多取前几个，卡片展示）</param>
public sealed record LibraryItemResponse(
    int TmdbId,
    string MediaType,
    string? Title,
    int? Year,
    double? Rating,
    string? CategoryName,
    int FileCount,
    int MissingFileCount,
    DateTimeOffset? LatestArchivedAt,
    bool HasPoster,
    IReadOnlyList<string> Genres);

/// <summary>媒体库分页结果</summary>
public sealed record LibraryListPage(
    IReadOnlyList<LibraryItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

// ---------- 作品详情（GET /library/{tmdbId}） ----------

/// <summary>作品详情中的单文件记录行</summary>
/// <param name="Id">媒体记录主键（点击行进 /media/{Id} 看处理时间线详情）</param>
/// <param name="FileExists">归档/源文件当前是否存在（详情读取时实时计算；null=无路径可判）</param>
public sealed record LibraryFileItem(
    long Id,
    string FileName,
    long FileSize,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    MediaItemStatus Status,
    ParseSource? ParseSource,
    double? Confidence,
    string? TargetPath,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset UpdatedAt,
    bool? FileExists);

/// <summary>演职员条目（cast 用 Character；crew 用 Job）</summary>
public sealed record WorkCreditDto(int PersonId, string Name, string? ProfilePath, string? Character, string? Job);

/// <summary>公司/电视台条目</summary>
public sealed record WorkCompanyDto(int Id, string Name, string? LogoPath, string? OriginCountry);

/// <summary>类型条目</summary>
public sealed record WorkGenreDto(int Id, string Name);

/// <summary>关键词条目</summary>
public sealed record WorkKeywordDto(int Id, string Name);

/// <summary>季摘要（含本地已归档集数）</summary>
public sealed record WorkSeasonDto(
    int SeasonNumber,
    string? Name,
    string? Overview,
    string? PosterPath,
    DateTimeOffset? AirDate,
    int EpisodeCount,
    int LocalFileCount);

/// <summary>作品详情：作品级富化元数据 + 关联信息 + 计数汇总 + 该作品全部文件记录</summary>
/// <remarks>
/// 打开时惰性富化（EnrichAsync）：Media_Work 缺失或过期则拉 TMDB 富化数据。富化失败（无 ApiKey/网络）时降级返回已有数据。
/// 文件记录含失败/待确认兄弟集，按季/集排序，FileExists 实时计算。写操作复用 /history/* 端点。
/// </remarks>
public sealed record LibraryWorkDetailResponse(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? Tagline,
    IReadOnlyList<string>? OriginCountry,
    string? OriginalLanguage,
    string? TmdbStatus,
    string? Homepage,
    int? Runtime,
    double? VoteAverage,
    int? VoteCount,
    DateTimeOffset? ReleaseDate,
    int? TotalSeasons,
    int? TotalEpisodes,
    string? CategoryName,
    bool HasPoster,
    bool HasBackdrop,
    string? BackdropPath,
    IReadOnlyList<WorkGenreDto> Genres,
    IReadOnlyList<WorkCompanyDto> Companies,
    IReadOnlyList<WorkCompanyDto> Networks,
    IReadOnlyList<WorkKeywordDto> Keywords,
    IReadOnlyList<WorkCreditDto> Cast,
    IReadOnlyList<WorkCreditDto> Crew,
    IReadOnlyList<WorkSeasonDto> Seasons,
    int FileCount,
    int CompletedCount,
    int FailedCount,
    int AwaitingReviewCount,
    int OtherCount,
    long TotalFileSize,
    DateTimeOffset? LatestArchivedAt,
    IReadOnlyList<LibraryFileItem> Files);

// ---------- 分季分集（GET /library/{tmdbId}/seasons/{seasonNumber}） ----------

/// <summary>单集（含本地映射）</summary>
/// <param name="HasLocalFile">本地是否已归档该集</param>
/// <param name="LocalStatus">本地文件状态（HasLocalFile 时有值）</param>
/// <param name="LocalFileExists">本地文件当前是否存在（实时）</param>
public sealed record LibraryEpisodeDto(
    int EpisodeNumber,
    string? Name,
    string? Overview,
    string? StillPath,
    DateTimeOffset? AirDate,
    int? Runtime,
    double? VoteAverage,
    bool HasLocalFile,
    MediaItemStatus? LocalStatus,
    bool? LocalFileExists);

/// <summary>某季分集响应</summary>
public sealed record LibrarySeasonResponse(
    int SeasonNumber,
    string? Name,
    string? Overview,
    IReadOnlyList<LibraryEpisodeDto> Episodes);

// ---------- 相关作品 / 筛选项 / 存在性扫描 ----------

/// <summary>相关作品卡片（按共享 类型/演职员/公司 计分）</summary>
public sealed record LibraryRelatedItem(
    int TmdbId,
    string MediaType,
    string? Title,
    int? Year,
    double? Rating,
    bool HasPoster,
    int SharedScore);

/// <summary>筛选项条目（id 对维度 = TMDB id；对国家/语言 = 代码）</summary>
public sealed record FacetRef(string Id, string Name, int Count);

/// <summary>媒体库可用筛选项（仅含库内已富化作品出现过的维度）</summary>
public sealed record LibraryFacets(
    IReadOnlyList<FacetRef> Genres,
    IReadOnlyList<FacetRef> Persons,
    IReadOnlyList<FacetRef> Companies,
    IReadOnlyList<FacetRef> Networks,
    IReadOnlyList<FacetRef> Keywords,
    IReadOnlyList<FacetRef> Countries,
    IReadOnlyList<FacetRef> Languages);

/// <summary>整库存在性扫描结果</summary>
public sealed record ScanExistenceResult(int Checked, int Missing);

/// <summary>存量音频重扫结果</summary>
/// <param name="Checked">本次纳入重扫的记录数（未探测过的已完成记录）</param>
/// <param name="Probed">成功探测并打标的记录数</param>
/// <param name="Incompatible">其中含不兼容音轨(av3a 等)的记录数</param>
/// <param name="Skipped">跳过数（文件缺失 / ffprobe 失败，未打标，下次可重试）</param>
public sealed record AudioRescanResult(int Checked, int Probed, int Incompatible, int Skipped);

/// <summary>孤儿文件：位于某分类归档根、但 DB 无对应记录的媒体文件（典型来源：删了记录但文件留在库里）</summary>
/// <param name="Path">孤儿文件绝对路径</param>
/// <param name="FileName">文件名</param>
/// <param name="Size">字节大小</param>
/// <param name="CategoryId">所在分类 Id</param>
/// <param name="CategoryName">所在分类名</param>
/// <param name="TmdbId">从文件 / 父目录 {tmdb-NNN} 标记解析的 TMDB id（无标记为 null，仅供展示参考）</param>
public sealed record OrphanFileDto(string Path, string FileName, long Size, long CategoryId, string CategoryName, int? TmdbId);

/// <summary>孤儿扫描结果</summary>
public sealed record OrphanScanResult(int Total, IReadOnlyList<OrphanFileDto> Items);

/// <summary>孤儿认领请求：把选中孤儿文件重新投入处理管线重新登记入库</summary>
public sealed record ClaimOrphansRequest(IReadOnlyList<string>? Paths = null);

/// <summary>孤儿认领结果</summary>
/// <param name="Total">请求认领的文件数</param>
/// <param name="Admitted">成功入队重新处理的数</param>
/// <param name="Skipped">跳过数（文件已不存在 / 已有同源记录在处理）</param>
public sealed record ClaimOrphansResult(int Total, int Admitted, int Skipped);
