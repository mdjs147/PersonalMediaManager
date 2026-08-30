using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Review;

// ---------- List ----------

/// <summary>待人工确认列表查询参数</summary>
/// <param name="Page">页码（从 1 开始）</param>
/// <param name="PageSize">每页条数（上限 100）</param>
/// <param name="ParseSource">可选过滤：Rule / Ai / Hybrid</param>
public sealed record ReviewListQuery(
    [Range(1, int.MaxValue, ErrorMessage = "page 必须 ≥ 1")]
    int Page = 1,
    int PageSize = 20,
    ParseSource? ParseSource = null);

/// <summary>待人工确认列表分页结果</summary>
public sealed record ReviewListPage(
    IReadOnlyList<ReviewItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>列表行项（含 TMDB 候选展示用）</summary>
/// <remarks>
/// Reason 为进入待确认队列的原因（前端用于筛选与提示，可空表示历史数据未填）；
/// AiInvolved = 本记录处理过程动用过 AI——前端据此区分「TMDB 零结果」两种形态：
/// false = 规则高置信多查询全零（TMDB 未收录，将每日自动重试），true = AI 参与后仍零结果（仅人工）。
/// </remarks>
public sealed record ReviewItemResponse(
    long Id,
    string FileName,
    string SourcePath,
    ParseSource? ParseSource,
    double? Confidence,
    string? ParsedInfo,
    IReadOnlyList<ReviewTmdbCandidate> TmdbCandidates,
    long RowVersion,
    DateTimeOffset CreatedAt,
    ReviewReason? Reason,
    bool AiInvolved = false);

public sealed record ReviewTmdbCandidate(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? PosterUrl,
    int? TotalSeasons);

// ---------- Confirm ----------

/// <summary>确认归档请求；EpisodeEnd 用于双集 / 多集合并文件归档为 SxxEaa-Ebb</summary>
public sealed record ConfirmRequest(
    int TmdbId,
    string MediaType,
    long CategoryId,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    long RowVersion,
    int? EpisodeEnd = null);

public sealed record ConfirmResult(long Id, MediaItemStatus Status, long RowVersion);

// ---------- Ignore ----------

public sealed record IgnoreRequest(long RowVersion, string? Reason);
public sealed record IgnoreResult(long Id, MediaItemStatus Status);

// ---------- Batch Confirm ----------

public sealed record BatchConfirmRequest(
    [Required(ErrorMessage = "items 不能为空")]
    [MinLength(1, ErrorMessage = "items 不能为空")]
    [MaxLength(50, ErrorMessage = "items 数量超过上限 50")]
    IReadOnlyList<BatchConfirmItem> Items);

public sealed record BatchConfirmItem(
    long Id,
    int TmdbId,
    string MediaType,
    long CategoryId,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    long RowVersion,
    int? EpisodeEnd = null);

/// <summary>批量确认结果（部分失败也返 200，失败明细放 Failed 数组）</summary>
public sealed record BatchConfirmResult(
    IReadOnlyList<long> Succeeded,
    IReadOnlyList<BatchConfirmFailure> Failed);

public sealed record BatchConfirmFailure(long Id, string Message);

// ---------- Batch Ignore ----------

/// <summary>批量忽略请求（共享 Reason，可空）</summary>
/// <remarks>每条仅需 Id + RowVersion 做乐观并发；Reason 是共享文案，写入每条 ErrorMessage 字段</remarks>
public sealed record BatchIgnoreRequest(
    [Required(ErrorMessage = "items 不能为空")]
    [MinLength(1, ErrorMessage = "items 不能为空")]
    [MaxLength(50, ErrorMessage = "items 数量超过上限 50")]
    IReadOnlyList<BatchIgnoreItem> Items,
    string? Reason);

public sealed record BatchIgnoreItem(long Id, long RowVersion);

/// <summary>批量忽略结果（部分失败也返 200，失败明细放 Failed 数组）</summary>
public sealed record BatchIgnoreResult(
    IReadOnlyList<long> Succeeded,
    IReadOnlyList<BatchIgnoreFailure> Failed);

public sealed record BatchIgnoreFailure(long Id, string Message);

// ---------- TMDB Search ----------

public sealed record TmdbSearchListQuery(
    [RequiredNotBlank(ErrorMessage = "搜索词不能为空")]
    string Query,
    string Type = "both",
    int? Year = null);

public sealed record TmdbSearchListResult(IReadOnlyList<TmdbSearchListItem> Items);

public sealed record TmdbSearchListItem(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? PosterUrl,
    IReadOnlyList<string>? OriginCountry,
    double Popularity);

// ---------- Bind TMDB ----------

public sealed record BindTmdbRequest(int TmdbId, string MediaType, long RowVersion);

public sealed record BindTmdbResult(long Id, int TmdbId, string? Title, int? Year, long RowVersion);

// ---------- TMDB Detail (按 ID 取详情) ----------

/// <summary>按 TMDB ID 取详情查询参数（人工填 ID 预览用）</summary>
public sealed record TmdbDetailQuery(int TmdbId, string MediaType);

/// <summary>剧集单季集数 + 季名（前端绝对集号换算 + 篇章对照用）</summary>
/// <remarks>SeasonNumber=0 为特别篇；EpisodeCount 来自 TMDB seasons[].episode_count，未播季可能为 0；Name 为季名（zh-CN 优先），供前端拿篇章标题（如「锻刀村篇」）对照选季。</remarks>
public sealed record ReviewSeasonEpisodeCount(int SeasonNumber, int EpisodeCount, string? Name = null);

/// <summary>按 ID 取得的 TMDB 详情（形状对齐搜索结果项 + TotalSeasons + 逐季集数，便于前端复用候选卡片与绝对集号换算）</summary>
public sealed record TmdbDetailItem(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? PosterUrl,
    int? TotalSeasons,
    IReadOnlyList<string>? OriginCountry,
    IReadOnlyList<ReviewSeasonEpisodeCount>? Seasons = null);

// ---------- Preview Path (去向预览) ----------

/// <summary>去向预览批量请求（只读，不落库、不调 TMDB）</summary>
/// <remarks>
/// 逐项用各自字段 + 分类 TargetRoot 经 PlexNamingConventions 算 Plex 相对 / 完整路径；
/// 标题 / 年份 / 季集不全或命名校验失败时该项返回 Error 文案而非整批抛异常。
/// 与确认走同一命名规范，故预览即确认后的真实落点。
/// </remarks>
public sealed record ReviewPreviewPathRequest(
    [Required(ErrorMessage = "items 不能为空")]
    [MinLength(1, ErrorMessage = "items 不能为空")]
    [MaxLength(500, ErrorMessage = "items 数量超过上限 500")]
    IReadOnlyList<ReviewPreviewPathItem> Items);

/// <summary>去向预览单项入参</summary>
/// <param name="Key">前端回填用幂等键（一般为 MediaItemId 字符串）</param>
/// <param name="FileName">用于取扩展名（无扩展名回退 .mkv 占位）</param>
public sealed record ReviewPreviewPathItem(
    string Key,
    int TmdbId,
    string MediaType,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    long CategoryId,
    string? FileName);

/// <summary>去向预览批量结果</summary>
public sealed record ReviewPreviewPathResult(IReadOnlyList<ReviewPreviewPathEntry> Entries);

/// <summary>去向预览单项结果（RelativePath/FullPath 与 Error 互斥：能算出路径则 Error 为 null）</summary>
public sealed record ReviewPreviewPathEntry(
    string Key,
    string? RelativePath,
    string? FullPath,
    string? Error);

// ---------- Check Files (文件存在性检查) ----------

/// <summary>文件检查请求：逐项 Id + RowVersion，校验源文件是否仍存在</summary>
/// <remarks>源文件已不存在的项转 Ignored 移出待确认队列（保留记录 + 原因）；每项走乐观并发 RowVersion 校验。</remarks>
public sealed record CheckFilesRequest(
    [Required(ErrorMessage = "items 不能为空")]
    [MinLength(1, ErrorMessage = "items 不能为空")]
    [MaxLength(500, ErrorMessage = "items 数量超过上限 500")]
    IReadOnlyList<CheckFileItem> Items);

public sealed record CheckFileItem(long Id, long RowVersion);

/// <summary>文件检查结果（部分失败也返 200）</summary>
/// <param name="Removed">源文件已不存在、已移出队列的 Id</param>
/// <param name="Kept">源文件仍存在、保留在队列的条数</param>
/// <param name="Failed">并发冲突 / 状态非待确认 / 记录不存在等未处理项</param>
public sealed record CheckFilesResult(
    IReadOnlyList<long> Removed,
    int Kept,
    IReadOnlyList<CheckFileFailure> Failed);

public sealed record CheckFileFailure(long Id, string Message);
