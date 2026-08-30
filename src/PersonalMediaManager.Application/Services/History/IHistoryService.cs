using PersonalMediaManager.Application.Dtos.History;

namespace PersonalMediaManager.Application.Services.History;

/// <summary>历史记录服务（D7.8）— API §2.6 list + detail + rescan</summary>
public interface IHistoryService
{
    Task<HistoryListPage> ListAsync(HistoryListQuery query, CancellationToken ct = default);

    /// <summary>处理队列：列出全部在途任务（排队 + 处理中），按 Id 正序（先进先出）</summary>
    /// <remarks>
    /// 状态集合取 MediaItemStatusGroups.InFlight（不含待确认与终态），与 Dashboard「队列长度」同口径；
    /// 复用 HistoryItemResponse / HistoryListPage 出参结构。
    /// </remarks>
    Task<HistoryListPage> ListPendingAsync(PendingListQuery query, CancellationToken ct = default);

    /// <summary>单条历史详情：MediaItem 全字段 + 关联 Tmdb 元数据 + AI 调用记录</summary>
    Task<MediaItemDetailResponse> GetDetailAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>取消指定排队或归档前正在处理的任务，并等待活动管线安全退出后落 Cancelled 终态</summary>
    Task<CancelTaskResult> CancelAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>把指定记录重置回 Queued 并重入主管线（仅 Failed 允许）</summary>
    Task<RescanResult> RescanAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>把所有 Failed 记录批量重置回 Queued 并重入主管线；源文件已不存在的跳过</summary>
    Task<BatchRescanResult> RescanAllFailedAsync(CancellationToken ct = default);

    /// <summary>重新处理：删旧记录后以全新检测重投源文件（单条 / 批量 / 整剧）</summary>
    /// <remarks>
    /// 仅终态记录（Completed/Skipped/Ignored/Cancelled/Failed）可重新处理；源文件须仍存在。
    /// 删除旧记录（含处理时间线，Ai 调用解绑保留）后重投——ProcessFileService 按 SourcePath 找不到即全新建项，
    /// 故得到一次彻底的全新解析归档，规避「同 SourcePath 已存在 → 扫描跳过」的重入死锁。
    /// 整剧（TmdbId）展开时只取该 TMDB 下的终态记录，在途/待确认的同剧集子项自动略过。
    /// </remarks>
    Task<HistoryBatchResult> ReprocessAsync(HistoryBatchRequest req, CancellationToken ct = default);

    /// <summary>删除历史记录（单条 / 批量 / 整剧）；仅终态可删，处理时间线级联删</summary>
    /// <remarks>
    /// 仅终态记录可删；在途 / 待确认记录拒绝（避免删正在被管线处理的记录）。
    /// 关联 Process_Step 随主记录级联删除，Audit_AiCall.MediaItemId 置 NULL（审计统计保留、解绑）。
    /// 整剧（TmdbId）展开口径同 ReprocessAsync。
    /// </remarks>
    Task<HistoryBatchResult> DeleteAsync(HistoryBatchRequest req, CancellationToken ct = default);

    /// <summary>批量退回重改（单条 / 批量 / 整剧）；仅 Completed/Skipped 可退回，其余计入失败明细</summary>
    /// <remarks>
    /// 逐条复用 ReopenForReviewAsync（各自独立 DbContext + 完整文件移回事务），串行执行不踩 EF 并发红线；
    /// 单条失败（状态不符 / 文件缺失等）被收进 Failed，不阻断其余条目。整剧（TmdbId）展开口径同 ReprocessAsync，
    /// 展开出的非 Completed/Skipped 终态（Ignored/Failed）会被单条拒绝并落入 Failed。
    /// </remarks>
    Task<HistoryBatchResult> BatchReopenAsync(HistoryBatchRequest req, CancellationToken ct = default);

    /// <summary>批量撤销归档（单条 / 批量 / 整剧）；仅 Completed 可撤销，其余计入失败明细</summary>
    /// <remarks>
    /// 逐条复用 UndoArchiveAsync（独立 DbContext + 文件移回 / 删副本事务），串行执行不踩 EF 并发红线；
    /// 单条失败被收进 Failed 不阻断其余。整剧（TmdbId）展开口径同 ReprocessAsync。
    /// </remarks>
    Task<HistoryBatchResult> BatchUndoArchiveAsync(HistoryBatchRequest req, CancellationToken ct = default);

    /// <summary>读取已归档媒体记录目标目录下的 .nfo 字节内容</summary>
    /// <remarks>
    /// .nfo 路径 = Path.ChangeExtension(MediaItem.TargetPath, ".nfo")。
    /// 未归档 / 目标路径缺失 / .nfo 不存在 → BusinessException。
    /// </remarks>
    Task<NfoDownloadResult> GetNfoAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>测试路径：只读演算该记录的归档去向并逐项体检（不动文件、不写库）</summary>
    /// <remarks>
    /// 用记录现有 ParsedInfo / TmdbId / CategoryId 走与 ArchiveService 一致的命名 + 路径穿越校验，
    /// 但全程 try/catch：精确暴露「越界集号 / episodeEnd&lt;episode / 缺季集 / 缺年份 / 路径越界」等命名报错，
    /// 供用户在「手动移动」前先看清归档去向是否正常。记录不存在 → BusinessException。
    /// </remarks>
    Task<ArchivePreviewResult> PreviewArchiveAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>手动移动：用记录现有元数据直接重跑归档步骤（Move / Copy），不重新解析</summary>
    /// <remarks>
    /// 区别于 Rescan（重投整条解析管线）：仅 Failed / Skipped / AwaitingReview 可手动移动，
    /// 经 MediaItem.BeginManualArchive 置 Archiving 后调 IArchiveService 实移，成功转 Completed/Skipped、失败 MarkFailed。
    /// 缺 TmdbId / CategoryId / ParsedInfo / 源文件不存在 → BusinessException（提示先重新处理或在审核页确认）。
    /// </remarks>
    Task<ManualArchiveResult> ManualArchiveAsync(long mediaItemId, ManualArchiveRequest req, CancellationToken ct = default);

    /// <summary>撤销最近归档：把归档文件移回源位（或删归档副本），并把记录回退为 Skipped</summary>
    /// <remarks>
    /// 仅 Completed 记录可撤销。撤销动作按记录最近一条 Archiving 时间线的 operation 推断 + 文件系统状态兜底：
    ///   - 当初 Move（或 Copy 但源已不在，避免删唯一副本）→ 反向 move 归档文件回源位（连同 stem 字幕、删本次生成的 .nfo）；
    ///   - 当初 Copy 且源仍在 → 删归档副本（不动源；共用 tvshow.nfo / poster.jpg 不删）。
    /// 选 Skipped 终态（非 Queued 重投）：避免按原规则立刻重新归档回同一错误位置使撤销形同虚设；
    /// 用户随后可改规则后主动「重新处理」/「手动移动」。
    /// 归档文件已不存在 → BusinessException；Move 回源时源位被新文件占用（FileConflict）→ 中止不覆盖。
    /// </remarks>
    Task<UndoArchiveResult> UndoArchiveAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>退回重改：把已确认归档（Completed/Skipped）的记录退回人工确认队列重新填资料</summary>
    /// <remarks>
    /// 区别于撤销归档（UndoArchive 回 Skipped 终态、不再自动流转）：本动作回 AwaitingReview，记录重新进入人工确认队列。
    /// Completed：先把归档文件反向 move 回源位（同撤销归档）再回退状态；Skipped：文件本就在源位，仅校验源仍在后回退（不动文件）。
    /// 仅 Completed / Skipped 可退回；源 / 归档文件已不存在 → BusinessException。
    /// </remarks>
    Task<ReopenResult> ReopenForReviewAsync(long mediaItemId, CancellationToken ct = default);

    /// <summary>取已缓存海报的本地绝对路径（不存在返 null，供 Controller PhysicalFile / 404）</summary>
    /// <remarks>
    /// 路径 = AppPaths.PostersDir/{MediaItem.TmdbId}.jpg；记录不存在 / 无 TmdbId / 文件未缓存 → null。
    /// 海报由 TmdbSearchService.GetDetailsAsync 或归档兜底下载；存量未缓存记录返 null，前端降级占位图。
    /// </remarks>
    Task<string?> GetPosterPathAsync(long mediaItemId, CancellationToken ct = default);
}
