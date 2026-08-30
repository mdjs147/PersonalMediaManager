namespace PersonalMediaManager.Application.Dtos.Statistics;

/// <summary>统计分析总览（GET /statistics/overview 一次性聚合）</summary>
/// <remarks>
/// 时间范围 Range（30d/90d/1y/all）统一按归档时间 ArchivedAt 过滤整页：
/// - all = 不过滤（全量库构成）；30d/90d/1y = 仅统计该窗口内归档的 Completed 文件及其作品。
/// 口径分两类：
/// - 作品级维度（电影/剧集数、年代、评分、类型、国家、分类）= 窗口内「已完成作品」（有窗口内 Completed 文件的 MediaWork）。
/// - 文件级维度（文件数、总容量、入库趋势、存储 Top）= 窗口内 Completed 的 MediaItem。
/// 趋势 TrendGranularity 自适应：30d/90d 按天（day），1y/all 按月（month）。
/// 识别方式构成 Identification 亦为文件级维度（窗口内 Completed 的 MediaItem）。
/// 个人库规模可控，作品标量维度全量载入内存聚合（沿用 LibraryService.GetFacets 范式）。
/// </remarks>
public sealed record StatisticsOverview(
    string Range,
    string TrendGranularity,
    StatisticsSummary Summary,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<DecadeBucket> DecadeDistribution,
    IReadOnlyList<RatingBucket> RatingHistogram,
    IReadOnlyList<NamedCount> GenreTop,
    IReadOnlyList<NamedCount> CountryTop,
    IReadOnlyList<NamedCount> CategoryDistribution,
    IReadOnlyList<StorageWork> StorageTop,
    IdentificationStats Identification);

/// <summary>顶部 KPI 概览</summary>
/// <param name="MovieCount">库内电影作品数</param>
/// <param name="TvCount">库内剧集作品数</param>
/// <param name="WorkCount">库内作品总数（电影 + 剧集）</param>
/// <param name="FileCount">已归档完成文件数</param>
/// <param name="TotalSize">已归档完成文件总字节数</param>
/// <param name="AvgRating">库内已富化作品平均 TMDB 评分（无评分作品则 null）</param>
/// <param name="RatedCount">参与平均的有评分作品数</param>
/// <param name="OldestYear">库内最早作品年份（无年份则 null）</param>
/// <param name="NewestYear">库内最新作品年份（无年份则 null）</param>
public sealed record StatisticsSummary(
    int MovieCount,
    int TvCount,
    int WorkCount,
    int FileCount,
    long TotalSize,
    double? AvgRating,
    int RatedCount,
    int? OldestYear,
    int? NewestYear);

/// <summary>入库趋势点（桶粒度由 StatisticsOverview.TrendGranularity 决定）</summary>
/// <param name="Bucket">桶键：day 粒度 yyyy-MM-dd / month 粒度 yyyy-MM（UTC）</param>
/// <param name="Count">该桶内归档完成的文件数</param>
public sealed record TrendPoint(string Bucket, int Count);

/// <summary>年代分布桶</summary>
/// <param name="Decade">十年代起始年（2020 表示 2020~2029）</param>
/// <param name="Count">该年代作品数</param>
public sealed record DecadeBucket(int Decade, int Count);

/// <summary>评分分布桶</summary>
/// <param name="Label">区间标签（如 8~9）</param>
/// <param name="Count">落入该区间的作品数</param>
public sealed record RatingBucket(string Label, int Count);

/// <summary>名称 + 计数通用条目（类型 / 国家 / 分类共用）</summary>
public sealed record NamedCount(string Name, int Count);

/// <summary>占用空间 Top 作品</summary>
/// <param name="TmdbId">TMDB 主键（拼海报端点 /library/poster/{tmdbId}）</param>
/// <param name="MediaType">movie / tv</param>
/// <param name="Title">展示标题（可空）</param>
/// <param name="Year">年份（可空）</param>
/// <param name="Size">该作品已归档文件总字节数</param>
public sealed record StorageWork(int TmdbId, string MediaType, string? Title, int? Year, long Size);

/// <summary>识别方式构成与 AI / 人工参与度</summary>
/// <remarks>
/// 口径：窗口内完成归档的媒体文件（Status=Completed，随整页 Range 归档时间窗过滤），
/// Total = RuleCount + AiCount + HybridCount + ManualCount + OtherCount（五类互斥完备）。
/// AiInvolvedCount / ReviewInvolvedCount 是过程维度（与 ParseSource 结果维度正交，可与任一类叠加）；
/// 比率（AI 参与率 / 人工审核介入率）由前端按计数自行计算，后端只给计数。
/// </remarks>
/// <param name="Total">窗口内完成归档媒体数（分母基线）</param>
/// <param name="RuleCount">ParseSource=Rule（规则直查）数</param>
/// <param name="AiCount">ParseSource=Ai（AI 兜底）数</param>
/// <param name="HybridCount">ParseSource=Hybrid（复用 + AI 混合）数</param>
/// <param name="ManualCount">ParseSource=Manual（强制标识锚定）数</param>
/// <param name="OtherCount">ParseSource 为 null（未识别 / 早期数据）数</param>
/// <param name="AiInvolvedCount">处理过程动用过 AI（AiInvolved=true，真实发起过 AI 调用）数</param>
/// <param name="ReviewInvolvedCount">处理过程走过人工审核（时间线存在 AwaitingReview 步骤）数</param>
public sealed record IdentificationStats(
    int Total,
    int RuleCount,
    int AiCount,
    int HybridCount,
    int ManualCount,
    int OtherCount,
    int AiInvolvedCount,
    int ReviewInvolvedCount);
