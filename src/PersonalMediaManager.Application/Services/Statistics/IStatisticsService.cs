using PersonalMediaManager.Application.Dtos.Statistics;

namespace PersonalMediaManager.Application.Services.Statistics;

/// <summary>统计分析聚合服务</summary>
/// <remarks>只读：媒体库内容构成 + 长周期入库趋势 + 存储分析。一次调用产出整页数据，前端单次加载。</remarks>
public interface IStatisticsService
{
    /// <summary>聚合统计分析总览（库构成 / 趋势 / 存储）</summary>
    /// <param name="range">时间范围：30d / 90d / 1y / all（空或未知 → all 全量，按归档时间窗过滤整页）</param>
    Task<StatisticsOverview> GetOverviewAsync(string? range = null, CancellationToken ct = default);
}
