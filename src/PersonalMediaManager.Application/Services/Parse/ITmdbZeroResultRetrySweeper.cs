namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>TMDB 未收录（TmdbZeroResult）待确认项的每日自动重投</summary>
/// <remarks>
/// 服务对象：规则高置信 + 多查询全零结果、被 ProcessFileService 判定「TMDB 暂未收录」跳过 AI
/// 转入人工队列的记录（Status=AwaitingReview、ReviewReason=TmdbZeroResult、AiInvolved=false）。
/// 新番 / 新剧发布初期 TMDB 常滞后数日收录——本服务把这类记录按「距上次动作 ≥ 20 小时、
/// 入库不超过窗口天数」的节奏重新排队走全管线：收录后自动归档，全程零 AI 零人工；
/// 窗口耗尽仍未收录则停留人工队列（不再重投）。由 TmdbZeroResultRetryJob 周期驱动。
/// </remarks>
public interface ITmdbZeroResultRetrySweeper
{
    /// <summary>扫描到期记录并重新排队，返回本轮重投条数</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}
