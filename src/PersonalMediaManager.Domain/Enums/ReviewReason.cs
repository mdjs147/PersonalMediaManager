namespace PersonalMediaManager.Domain.Enums;

/// <summary>进入人工确认队列的原因（用于前端筛选与提示）</summary>
/// <remarks>
/// 仅在 MediaItem.Status 转为 AwaitingReview 时写入；其它状态该字段保持 null。
/// 由 ProcessFileService 在 7 个决策分支映射对应枚举值：
///   - TmdbMultiCandidate：TMDB 候选 &gt; 阈值 N（无法自动取舍）
///   - TmdbZeroResult：TMDB 候选 = 0（找不到匹配项）
///   - AiLowConfidence：AI 兜底失败或置信度不足
///   - CategoryUnresolved：规则/AI 均未命中分类映射
///   - NameCollision：归档阶段目标同名文件冲突，且冲突策略为「询问(Ask)」时转人工裁定是否覆盖
///   - ParseIncomplete：解析字段不全无法归档（如剧集缺 season / episode），需用户在审核界面补全
///   - HoldBeforeArchive：归档前拦截开关开启，自动匹配就绪也强制先经人工确认再归档
/// （ManualReopen 例外：非管线分支，由 History 退回重改写入。）
/// </remarks>
public enum ReviewReason
{
    /// <summary>未指定原因（默认值，不应入库 AwaitingReview 时使用）</summary>
    None = 0,

    /// <summary>TMDB 候选过多无法自动取舍</summary>
    TmdbMultiCandidate = 1,

    /// <summary>TMDB 零候选</summary>
    TmdbZeroResult = 2,

    /// <summary>AI 兜底失败或置信度不足</summary>
    AiLowConfidence = 3,

    /// <summary>分类规则未命中</summary>
    CategoryUnresolved = 4,

    /// <summary>归档阶段目标同名文件冲突，按「询问(Ask)」策略待人工裁定是否覆盖</summary>
    NameCollision = 5,

    /// <summary>解析字段不全无法归档（如剧集缺 season / episode）</summary>
    ParseIncomplete = 6,

    /// <summary>人工退回重改（已确认归档后用户发现填错，主动退回重新确认）</summary>
    ManualReopen = 7,

    /// <summary>归档前拦截开关（Archive_HoldBeforeArchive）开启：自动匹配已就绪，但强制先经人工确认再归档</summary>
    HoldBeforeArchive = 8,
}
