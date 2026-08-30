using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>主处理编排服务契约（被 TaskProcessorWorker 串行调用）</summary>
/// <remarks>
/// 单文件全链路：写入完成判定 → Parsing → TmdbMatching → AiParsing → TmdbRematching → Classifying → Archiving；
/// 失败也内部状态机转换（Failed / AwaitingReview），不抛业务异常给 Worker。
/// 实现：D7.1 ProcessFileService（依赖 IFileMover / ITmdbClient / IAiCallOrchestrator 等多个契约）。
/// Worker 责任：保证「同一时刻只有一个文件在处理」+ 转发异常到日志，不感知具体业务。
/// </remarks>
public interface IProcessFileService
{
    /// <summary>处理单个入队文件，返回处理结果概要（成功 / 失败 / 待人工）</summary>
    Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken cancellationToken);
}

/// <summary>处理结果概要（仅供 Worker 日志统计；详细状态走 MediaItem.Status）</summary>
/// <param name="MediaItemId">主键，0 表示路径已存在重复入队被去重忽略</param>
/// <param name="Outcome">终态分类</param>
public sealed record ProcessFileOutcome(long MediaItemId, ProcessOutcome Outcome);

public enum ProcessOutcome
{
    /// <summary>归档完成（MediaItem.Status=Completed）</summary>
    Completed = 1,
    /// <summary>等待人工确认（MediaItem.Status=AwaitingReview）</summary>
    AwaitingReview = 2,
    /// <summary>处理失败（MediaItem.Status=Failed）</summary>
    Failed = 3,
    /// <summary>同路径已在处理 / 已归档，本次入队被忽略（幂等）</summary>
    Skipped = 4,
}
