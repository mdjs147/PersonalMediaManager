namespace PersonalMediaManager.Domain.Enums;

/// <summary>媒体状态分组（在途/排队/处理中集合，单一事实来源）</summary>
/// <remarks>
/// 统一「排队 / 处理中 / 在途」三组状态的定义，供 Dashboard「队列长度」统计与「处理队列」列表共用，
/// 避免两处各自硬编码导致口径漂移。新增中间态时只改此处一份。
///
/// - Pending：已发现 / 已入队但尚未真正开跑（Detected / Queued）。
/// - Running：管线正在处理中的各阶段（解析 / TMDB / AI / 分类 / 归档）。
/// - InFlight：Pending ∪ Running，即「还没到终态、且不在人工确认队列」的全部在途任务。
    ///   有意排除 AwaitingReview（它有独立的「待确认队列」入口）与所有终态（Completed/Skipped/Ignored/Cancelled/Failed）。
/// </remarks>
public static class MediaItemStatusGroups
{
    /// <summary>已发现 / 已入队，尚未开跑</summary>
    public static readonly MediaItemStatus[] Pending =
    {
        MediaItemStatus.Detected, MediaItemStatus.Queued,
    };

    /// <summary>管线处理中的各阶段</summary>
    public static readonly MediaItemStatus[] Running =
    {
        MediaItemStatus.Parsing, MediaItemStatus.TmdbMatching, MediaItemStatus.AiParsing,
        MediaItemStatus.TmdbRematching, MediaItemStatus.Classifying, MediaItemStatus.Archiving,
    };

    /// <summary>全部在途任务（排队 + 处理中，不含待确认与终态）</summary>
    public static readonly MediaItemStatus[] InFlight =
    {
        MediaItemStatus.Detected, MediaItemStatus.Queued,
        MediaItemStatus.Parsing, MediaItemStatus.TmdbMatching, MediaItemStatus.AiParsing,
        MediaItemStatus.TmdbRematching, MediaItemStatus.Classifying, MediaItemStatus.Archiving,
    };

    /// <summary>允许用户安全取消的在途状态（归档文件操作开始后不允许取消）</summary>
    /// <remarks>
    /// Archiving 可能已完成同卷 File.Move；此时强制取消会让数据库终态与磁盘现实不一致，故必须让归档收尾。
    /// </remarks>
    public static readonly MediaItemStatus[] Cancellable =
    {
        MediaItemStatus.Detected, MediaItemStatus.Queued,
        MediaItemStatus.Parsing, MediaItemStatus.TmdbMatching, MediaItemStatus.AiParsing,
        MediaItemStatus.TmdbRematching, MediaItemStatus.Classifying,
    };

    /// <summary>终态集合（已完成 / 已跳过 / 已忽略 / 已取消 / 失败）</summary>
    /// <remarks>与 MediaItem.IsTerminal() 同口径；供「删除记录 / 重新处理」按 TmdbId 展开整剧时只取可操作的终态记录。</remarks>
    public static readonly MediaItemStatus[] Terminal =
    {
        MediaItemStatus.Completed, MediaItemStatus.Skipped,
        MediaItemStatus.Ignored, MediaItemStatus.Cancelled, MediaItemStatus.Failed,
    };
}
