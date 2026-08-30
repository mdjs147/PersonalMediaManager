using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.History;

/// <summary>历史列表查询参数（GET /history）</summary>
/// <param name="Page">页码（≥ 1）</param>
/// <param name="PageSize">每页（上限 100）</param>
/// <param name="Status">状态过滤（Completed/Skipped/Ignored/Cancelled/Failed）</param>
/// <param name="ParseSource">解析来源过滤</param>
/// <param name="CategoryId">分类过滤</param>
/// <param name="From">起始时间（按 UpdatedAt）</param>
/// <param name="To">结束时间（按 UpdatedAt）</param>
/// <param name="Keyword">文件名/标题模糊匹配</param>
public sealed record HistoryListQuery(
    [Range(1, int.MaxValue, ErrorMessage = "分页参数非法")]
    int Page = 1,
    int PageSize = 20,
    MediaItemStatus? Status = null,
    ParseSource? ParseSource = null,
    long? CategoryId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Keyword = null);

/// <summary>处理队列列表查询参数（GET /history/pending）</summary>
/// <param name="Page">页码（≥ 1）</param>
/// <param name="PageSize">每页（上限 100）</param>
/// <remarks>
/// 处理队列固定展示在途状态集合（MediaItemStatusGroups.InFlight），不开放状态过滤；
/// 排序固定按 Id 正序（先进先出），复用 HistoryItemResponse / HistoryListPage 出参结构。
/// </remarks>
public sealed record PendingListQuery(
    [Range(1, int.MaxValue, ErrorMessage = "分页参数非法")]
    int Page = 1,
    int PageSize = 20);

public sealed record HistoryListPage(
    IReadOnlyList<HistoryItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>历史列表行（r1 P1-8 平铺 FileSize / Season / Episode / EpisodeEnd 避免前端解析 ParsedInfo）</summary>
/// <remarks>
/// Title/Year/Season/Episode/EpisodeEnd 由后端从 MediaItem.ParsedInfo JSON 解析后回填，损坏字段为 null；
/// FileSize 直接读 MediaItem.FileSize 列（字节）；前端用 fmtSize 显示。
/// EpisodeEnd 仅双集 / 多集合并文件（SxxEaa-Ebb）非 null；单集为 null。
/// MetadataPending=true 表示「视频已落地但 nfo / 海报 / Webhook 等元数据写入失败」（归档降级容错），
/// 由 ListAsync 按该记录最后一条 stage=Archiving 时间线步骤的 detail 判定；
/// 处理队列（ListPendingAsync）行恒为 false（在途任务尚无归档结果）。
/// </remarks>
public sealed record HistoryItemResponse(
    long Id,
    string FileName,
    long FileSize,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    MediaItemStatus Status,
    ParseSource? ParseSource,
    double? Confidence,
    string? Category,
    string? TargetPath,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset UpdatedAt,
    int? TmdbId = null,
    string? TmdbMediaType = null,
    bool MetadataPending = false,
    bool HasIncompatibleAudio = false);

public sealed record RescanResult(long Id, MediaItemStatus Status);

/// <summary>取消排队或正在处理任务的结果</summary>
public sealed record CancelTaskResult(long Id, MediaItemStatus Status);

/// <summary>批量重试结果</summary>
/// <param name="Requeued">成功重投回队列的记录数</param>
/// <param name="Skipped">因源文件已不存在而跳过的记录数</param>
public sealed record BatchRescanResult(int Requeued, int Skipped);

// ---------- 重新处理 / 删除记录（单条 / 批量 / 整剧统一入口）----------

/// <summary>重新处理 / 删除目标选择器（三选一：显式 Ids ＞ 整剧 TmdbId）</summary>
/// <remarks>
/// 三种模式共用一个请求体：
///   - 单条：Ids = [123]
///   - 批量：Ids = [123, 456, ...]
///   - 整剧：TmdbId = 12345（+ 可选 TmdbMediaType="tv"/"movie"），服务端展开为该 TMDB 下全部「终态」记录。
/// Ids 非空时优先用 Ids，忽略 TmdbId；两者都空抛 BusinessError。
/// </remarks>
public sealed record HistoryBatchRequest(
    IReadOnlyList<long>? Ids = null,
    int? TmdbId = null,
    string? TmdbMediaType = null);

/// <summary>重新处理 / 删除批量结果（部分失败也返 200，失败明细放 Failed）</summary>
/// <param name="Total">本次实际处理的目标条数（Ids 长度或整剧展开后的终态条数）</param>
public sealed record HistoryBatchResult(
    int Total,
    IReadOnlyList<long> Succeeded,
    IReadOnlyList<HistoryBatchFailure> Failed);

public sealed record HistoryBatchFailure(long Id, string Message);

/// <summary>.nfo 下载内容（字节流 + 建议文件名 + MIME）</summary>
/// <remarks>
/// .nfo 文件通常 &lt;10KB，整文件读字节数组返回比 Stream 简化生命周期；
/// FileName 沿用磁盘文件名供浏览器 Content-Disposition 用。
/// </remarks>
public sealed record NfoDownloadResult(byte[] Content, string FileName, string ContentType = "application/xml; charset=utf-8");

/// <summary>历史详情响应（GET /history/{id}）</summary>
/// <remarks>
/// 单条 MediaItem 的全字段 + 关联 TMDB 元数据（如有缓存命中）+ 关联 AI 调用列表（Audit_AiCall）。
/// 候选集（TmdbCandidates）在 TmdbSearch 阶段实时计算，不持久化，故详情仅暴露最终命中的元数据。
/// </remarks>
public sealed record MediaItemDetailResponse(
    long Id,
    string SourcePath,
    string FileName,
    long FileSize,
    string? FileHash,
    MediaItemStatus Status,
    ParseSource? ParseSource,
    double? Confidence,
    string? ParsedInfo,
    int? TmdbId,
    string? TmdbMediaType,
    long? CategoryId,
    string? Category,
    string? TargetPath,
    string? ErrorMessage,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TmdbMetadataSummary? Tmdb,
    IReadOnlyList<AiCallEntryResponse> AiCalls,
    IReadOnlyList<ProcessStepEntry> Steps);

/// <summary>处理时间线单步（Process_Step 行投影；前端 MediaDetail 时间线按 Stage 渲染 Detail）</summary>
/// <param name="Id">主键</param>
/// <param name="Stage">阶段（值与 MediaItemStatus 枚举一致；前端用此名做 STATUS_MAP 查表）</param>
/// <param name="StartedAt">进入该 Stage 的时刻（UTC）</param>
/// <param name="DurMs">该 Stage 实际耗时（毫秒，终态步骤 = 0）</param>
/// <param name="Detail">结构化 JSON（按 Stage 字段不同，前端按 Stage 分支渲染 + 缺字段降级到 "—"）</param>
public sealed record ProcessStepEntry(
    long Id,
    MediaItemStatus Stage,
    DateTimeOffset StartedAt,
    long DurMs,
    string? Detail);

/// <summary>关联的 TMDB 元数据摘要（TmdbMetadataCache 命中）</summary>
public sealed record TmdbMetadataSummary(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    int? TotalSeasons,
    string? PosterPath,
    IReadOnlyList<string>? OriginCountry,
    string? OriginalLanguage,
    string? Overview,
    DateTimeOffset CachedAt);

/// <summary>单次 AI 调用记录（Audit_AiCall）</summary>
public sealed record AiCallEntryResponse(
    long Id,
    long ProviderId,
    bool Success,
    int LatencyMs,
    string? ErrorType,
    string? ErrorDetail,
    DateTimeOffset Timestamp);

// ---------- 测试路径（只读归档去向体检）/ 手动移动 ----------

/// <summary>归档去向体检单项（前端逐条渲染：通过 / 失败 + 说明）</summary>
/// <param name="Name">检查项名称（如「源文件存在」「Plex 命名校验」）</param>
/// <param name="Ok">是否通过</param>
/// <param name="Detail">补充说明（通过时可为路径片段，失败时为具体原因）</param>
public sealed record ArchivePreviewCheck(string Name, bool Ok, string? Detail);

/// <summary>测试路径结果（POST /history/{id}/preview-archive）— 只读演算该记录归档去向并逐项体检</summary>
/// <remarks>
/// 用记录现有 ParsedInfo / TmdbId / CategoryId 走与 ArchiveService 完全一致的路径计算（ArchiveFolderResolver：
/// TMDB 规范标题回退链 + {tmdb-NNN} 已存在目录复用 + 多季季首播年 / 季标题，再加 PathSafetyGuard），
/// 但全程 try/catch 不动文件：精确暴露「最后一部 / 越界集号 / 缺字段」等导致的命名报错。
/// Title 为参与命名的最终标题（TMDB 中文名 → 原文名 → 解析名回退链结果），预览即真实落点。
/// CanArchive=true 表示路径算得出且无阻断项；Error 与 RelativePath/TargetPath 互斥（出错时后者为 null）。
/// </remarks>
public sealed record ArchivePreviewResult(
    long Id,
    MediaItemStatus Status,
    string SourcePath,
    bool SourceExists,
    long? CategoryId,
    string? CategoryName,
    string? TargetRoot,
    bool IsTv,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    string? RelativePath,
    string? TargetPath,
    bool TargetExists,
    bool CanArchive,
    string? Error,
    IReadOnlyList<ArchivePreviewCheck> Checks);

/// <summary>手动移动请求（POST /history/{id}/manual-archive）</summary>
/// <param name="Mode">文件操作：Move（移动，删源）/ Copy（复制，保留源）；缺省 Move</param>
public sealed record ManualArchiveRequest(string Mode = "Move");

/// <summary>手动移动结果</summary>
/// <param name="Id">记录 ID</param>
/// <param name="Status">归档后状态（Completed / Skipped）</param>
/// <param name="TargetPath">落地目标绝对路径</param>
/// <param name="Outcome">Completed=已落地 / Skipped=目标已存在同名（冲突跳过）</param>
/// <param name="Warnings">降级警告（nfo / Webhook 写入失败等「视频已落地但元数据待补」项；null / 空 = 无）</param>
/// <remarks>
/// Warnings 非空时仍属成功（Outcome=Completed）：视频主体已落地，仅元数据类步骤降级失败——
/// 前端应据此提示「已移动，但元数据待补」而非静默当作全量成功。
/// </remarks>
public sealed record ManualArchiveResult(
    long Id,
    MediaItemStatus Status,
    string? TargetPath,
    string Outcome,
    IReadOnlyList<string>? Warnings = null);

/// <summary>撤销归档结果（POST /history/{id}/undo-archive）</summary>
/// <param name="Id">记录 ID</param>
/// <param name="Status">撤销后状态（Skipped——已回退为未入库）</param>
/// <param name="RestoredPath">撤销去向：Move 撤销 = 归档文件移回的源路径；Copy 撤销 = 保留的源路径</param>
/// <param name="Operation">撤销动作：MOVE_BACK（归档文件移回源位）/ DELETE_COPY（删归档副本，源本就在）</param>
public sealed record UndoArchiveResult(
    long Id,
    MediaItemStatus Status,
    string RestoredPath,
    string Operation);

/// <summary>退回重改结果（POST /history/{id}/reopen）</summary>
/// <param name="Id">记录 ID</param>
/// <param name="Status">退回后状态（AwaitingReview——已回到人工确认队列）</param>
/// <param name="RestoredPath">源文件位置（Completed 退回 = 归档文件移回的源路径；Skipped = 本就在的源路径）</param>
/// <param name="Operation">动作：MOVE_BACK / DELETE_COPY（已完成退回）/ REOPEN_INPLACE（已跳过退回，文件不动）</param>
public sealed record ReopenResult(
    long Id,
    MediaItemStatus Status,
    string RestoredPath,
    string Operation);
