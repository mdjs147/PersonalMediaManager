using PersonalMediaManager.Domain.Aggregates.MediaItems;

namespace PersonalMediaManager.Application.Services.Classify;

/// <summary>分类服务契约（D7.3 实现）— 规则 → AI → 人工三档兜底</summary>
/// <remarks>
/// 输入：已确定 TmdbId / TmdbMediaType / Confidence / ParsedInfo 的 MediaItem；
/// 输出：CategoryId（命中分类）或 SendToReview（无规则命中且 AI 也不确定）。
/// 实现规则：优先按用户配置 Category_MatchRule（关键字 / 类型 / 国家 / 语言条件组合）；
/// 命中多个时按 CategoryDefinition.Priority；零命中走 AI 推荐；AI 也不确定则 SendToReview。
/// </remarks>
public interface IClassifyService
{
    Task<ClassifyResult> ClassifyAsync(MediaItem item, CancellationToken ct = default);
}

/// <param name="CategoryId">命中的分类主键；Decision=SendToReview 时为 null</param>
/// <param name="Decision">Matched=已确定分类；SendToReview=兜底失败需人工</param>
public sealed record ClassifyResult(long? CategoryId, ClassifyDecision Decision);

public enum ClassifyDecision
{
    /// <summary>命中分类，可继续 Archiving</summary>
    Matched = 1,
    /// <summary>所有兜底均失败，转 AwaitingReview</summary>
    SendToReview = 2,
}
