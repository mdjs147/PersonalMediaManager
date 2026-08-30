using PersonalMediaManager.Application.Dtos.Dashboard;

namespace PersonalMediaManager.Application.Services.Dashboard;

/// <summary>仪表盘聚合查询（D7.7）— API §2.4 stats + recent + tasks 三端点</summary>
public interface IDashboardService
{
    Task<DashboardStats> GetStatsAsync(CancellationToken ct = default);

    Task<DashboardRecentList> GetRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>24h 窗口内 Quartz Job 执行历史（按 StartedAt DESC）</summary>
    /// <param name="windowHours">回看时长（小时，1 ≤ x ≤ 168，默认 24）</param>
    /// <param name="limit">最多返回条数（1 ≤ x ≤ 200，默认 50）</param>
    Task<DashboardTasksResponse> GetTasksAsync(int windowHours, int limit, CancellationToken ct = default);

    /// <summary>7×24 活动热图：按 MediaItem.ArchivedAt 按 (DayOfWeek, Hour) 桶聚合</summary>
    /// <param name="from">起始（缺省 now-7d）；最早不能早于 now-90d</param>
    /// <param name="to">结束（缺省 now）</param>
    Task<DashboardHeatmapResponse> GetHeatmapAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);

    /// <summary>每监控目录最近 24h 归档活动 sparkline（小时桶）</summary>
    Task<DashboardWatchFolderActivityResponse> GetWatchFolderActivityAsync(CancellationToken ct = default);
}
