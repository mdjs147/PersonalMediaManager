using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Search;

/// <summary>统一全局搜索查询参数（GET /search）</summary>
/// <param name="Q">关键词（trim 后截断 100；空 / 纯空白 → 返回空结果不报错）</param>
/// <param name="LimitPerGroup">每域返回上限（钳到 1..20，缺省 5）</param>
/// <param name="Scopes">搜索域（逗号分隔 "history,library,review"；缺省 / 空 → 三域全查）</param>
/// <remarks>
/// 顶栏全局搜索框用：一次 GET 返回历史 / 媒体库 / 待确认三域各 topN 命中 + 各域真实总数。
/// 不分页（下拉浮层只展示 topN，「查看全部」跳到对应列表页带 keyword 走原页过滤）。
/// </remarks>
public sealed record GlobalSearchQuery(
    string? Q = null,
    int LimitPerGroup = 5,
    string? Scopes = null);

/// <summary>全局搜索结果（三域命中 + 各域总数）</summary>
/// <remarks>
/// Query 回显实际生效的关键词（trim + 截断后）；空关键词时三组均为空数组、Counts 全 0。
/// 未在 Scopes 内的域返回空数组、对应 Counts 计数为 0（未查询）。
/// </remarks>
public sealed record GlobalSearchResponse(
    string Query,
    IReadOnlyList<HistoryHit> History,
    IReadOnlyList<LibraryHit> Library,
    IReadOnlyList<ReviewHit> Review,
    SearchCounts Counts);

/// <summary>历史域命中（终态处理记录；跳详情用 Id → /media/{id}）</summary>
/// <remarks>Title / Year 由 MediaItem.ParsedInfo JSON 平铺；TmdbId / TmdbMediaType 供前端展示来源徽标。</remarks>
public sealed record HistoryHit(
    long Id,
    string FileName,
    string? Title,
    int? Year,
    MediaItemStatus Status,
    int? TmdbId,
    string? TmdbMediaType);

/// <summary>媒体库域命中（按 TMDB 作品聚合；跳详情用 TmdbId + MediaType → LibraryDetail）</summary>
/// <remarks>FileCount = 该作品键下已归档（Completed）文件数；HasPoster 仅对返回项算 File.Exists。</remarks>
public sealed record LibraryHit(
    int TmdbId,
    string MediaType,
    string? Title,
    int? Year,
    bool HasPoster,
    int FileCount);

/// <summary>待确认域命中（限定 AwaitingReview；跳详情用 Id → /media/{id}）</summary>
public sealed record ReviewHit(
    long Id,
    string FileName,
    string? Title,
    int? Year);

/// <summary>各域命中真实总数（用于下拉「查看全部 (N)」徽标；未查询的域为 0）</summary>
public sealed record SearchCounts(
    int History,
    int Library,
    int Review);
