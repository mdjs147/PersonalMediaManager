using PersonalMediaManager.Application.Dtos.Review;

namespace PersonalMediaManager.Application.Services.Review;

/// <summary>人工确认队列服务（D7.5）— list/confirm/ignore/batch/tmdb-search/bind-tmdb 全套</summary>
/// <remarks>
/// 仅处理 Status=AwaitingReview 的 MediaItem；
/// 写操作（confirm/ignore/bind）一律走乐观并发 RowVersion 检查，冲突抛 BusinessException
/// "记录已被其他用户修改，请刷新"（API 规范 §2.5 错误码表对齐）。
/// confirm 同步等 ArchiveService 完成（同卷 mv 是瞬时；跨卷大文件场景由后续优化承担）。
/// bind-tmdb 仅更新 TmdbId + 拉详情进缓存，不改 status，不触发归档（归档仍需调 confirm）。
/// </remarks>
public interface IReviewService
{
    Task<ReviewListPage> ListAsync(ReviewListQuery query, CancellationToken ct = default);

    Task<ConfirmResult> ConfirmAsync(long mediaItemId, ConfirmRequest req, CancellationToken ct = default);

    Task<IgnoreResult> IgnoreAsync(long mediaItemId, IgnoreRequest req, CancellationToken ct = default);

    Task<BatchConfirmResult> BatchConfirmAsync(BatchConfirmRequest req, CancellationToken ct = default);

    Task<BatchIgnoreResult> BatchIgnoreAsync(BatchIgnoreRequest req, CancellationToken ct = default);

    Task<TmdbSearchListResult> TmdbSearchAsync(long mediaItemId, TmdbSearchListQuery query, CancellationToken ct = default);

    Task<BindTmdbResult> BindTmdbAsync(long mediaItemId, BindTmdbRequest req, CancellationToken ct = default);

    Task<TmdbDetailItem> TmdbDetailAsync(long mediaItemId, TmdbDetailQuery query, CancellationToken ct = default);

    /// <summary>批量去向预览（只读）：逐项算 Plex 相对 / 完整路径，字段不全的单项返 Error 文案</summary>
    Task<ReviewPreviewPathResult> PreviewPathsAsync(ReviewPreviewPathRequest req, CancellationToken ct = default);

    /// <summary>文件检查：校验各项源文件是否仍存在，不存在的转 Ignored 移出队列（保留记录 + 原因）</summary>
    Task<CheckFilesResult> CheckFilesAsync(CheckFilesRequest req, CancellationToken ct = default);
}
