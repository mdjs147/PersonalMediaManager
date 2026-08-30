namespace PersonalMediaManager.Domain.Enums;

/// <summary>媒体处理状态机（需求文档 §6.1，共 14 个值）</summary>
/// <remarks>
/// 数值有意拉开间距（10 / 100 / 110 / 120 / 130），便于将来在中间插入新状态而不破坏已存数据。
/// 转移合法性由 MediaItem.Transition(MediaItemStatus next) 集中守护（D1.1），非法转移抛 DomainException。
/// </remarks>
public enum MediaItemStatus
{
    /// <summary>文件被发现，等待写入完成判定</summary>
    Detected = 0,

    /// <summary>写入完成，已入处理队列</summary>
    Queued = 10,

    /// <summary>规则引擎解析中</summary>
    Parsing = 20,

    /// <summary>TMDB 查询中（规则结果）</summary>
    TmdbMatching = 30,

    /// <summary>AI 兜底解析中</summary>
    AiParsing = 40,

    /// <summary>TMDB 二次查询（AI 结果）</summary>
    TmdbRematching = 50,

    /// <summary>分类判断中</summary>
    Classifying = 60,

    /// <summary>进入人工确认队列</summary>
    AwaitingReview = 70,

    /// <summary>文件操作 + nfo/海报生成中</summary>
    Archiving = 80,

    /// <summary>成功完成</summary>
    Completed = 100,

    /// <summary>同名冲突或被忽略规则跳过</summary>
    Skipped = 110,

    /// <summary>用户在确认队列手动忽略</summary>
    Ignored = 120,

    /// <summary>用户在处理详情中主动取消排队或正在处理的任务</summary>
    Cancelled = 125,

    /// <summary>处理异常（含 IO/网络/未预期）</summary>
    Failed = 130,
}
