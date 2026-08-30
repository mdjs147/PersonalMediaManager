using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Dashboard;

/// <summary>仪表盘统计聚合（GET /dashboard/stats）</summary>
public sealed record DashboardStats(
    DashboardTodayBucket Today,
    DashboardTotalBucket Total,
    DashboardQueueBucket Queue,
    DashboardParseSourceBucket ParseSource,
    DashboardServiceBucket Service);

public sealed record DashboardTodayBucket(int Processed, int Skipped, int Failed, int Review);

public sealed record DashboardTotalBucket(int Processed, int Skipped, int Failed);

public sealed record DashboardQueueBucket(int Review, int Running, int Pending);

public sealed record DashboardParseSourceBucket(int Rule, int Ai, int Hybrid);

public sealed record DashboardServiceBucket(bool Running, long UptimeSeconds);

/// <summary>仪表盘最近处理列表（GET /dashboard/recent）</summary>
public sealed record DashboardRecentList(IReadOnlyList<DashboardRecentItem> Items);

public sealed record DashboardRecentItem(
    long Id,
    string FileName,
    MediaItemStatus Status,
    string? Title,
    int? Year,
    string? Category,
    string? TargetPath,
    ParseSource? ParseSource,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset UpdatedAt);

/// <summary>仪表盘 24h 任务执行历史（GET /dashboard/tasks）</summary>
/// <remarks>
/// 数据源 Audit_ScheduledTaskRun，按 StartedAt DESC 取窗口内前 N 条。
/// 仅返回已发生的运行（含未结束的 Running 行）；未来计划性触发不在本结构内（前端如需另接 Quartz 调度查询）。
/// </remarks>
public sealed record DashboardTasksResponse(IReadOnlyList<ScheduledTaskRunEntry> Items);

/// <summary>单次 Quartz Job 执行行（Audit_ScheduledTaskRun 行投影）</summary>
public sealed record ScheduledTaskRunEntry(
    long Id,
    string JobKey,
    string? FireInstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    string Outcome,
    int? ProcessedCount,
    string? ErrorMessage,
    string? DetailJson);

/// <summary>仪表盘健康检查（GET /dashboard/health）</summary>
/// <remarks>
/// 4 项并行 + 各项独立 3s 超时：Kestrel uptime / TMDB ping / AI 主提供商 ping / SQLite size。
/// 任意一项超时 / 异常 → status=fail + detail 写错误摘要；未配置 → status=off。
/// 顺序固定：Kestrel / SQLite / TMDB / AI，便于前端按位渲染。
/// </remarks>
public sealed record DashboardHealthResponse(IReadOnlyList<HealthCheckEntry> Checks);

/// <summary>单项健康检查结果</summary>
/// <param name="Name">展示名（如 "Kestrel" / "TMDB" / "AI 主提供商" / "SQLite (pmm.db)"）</param>
/// <param name="Status">ok / warn / fail / off（与前端 dotColor 函数对齐）</param>
/// <param name="Detail">人类可读摘要（如 "上线 6d 14h" / "24.6 MB" / "200 OK · 215ms"）</param>
/// <param name="LatencyMs">仅外部 ping 类项目带（TMDB / AI），其余为 null</param>
public sealed record HealthCheckEntry(string Name, string Status, string? Detail, int? LatencyMs);

/// <summary>7×24 活动热图（GET /dashboard/heatmap）</summary>
/// <remarks>
/// rows: DayOfWeek (.NET 枚举：0=Sun..6=Sat)；cols: Hour 0..23（UTC，前端如需展示本地时区可在客户端做小时偏移）。
/// 数据源：Media_Item.ArchivedAt 不为空的记录，按 (DayOfWeek, Hour) 桶聚合 Count。
/// 窗口：调用方传 from / to（缺省时默认 now-7d ~ now，最长 90 天）。
/// </remarks>
public sealed record DashboardHeatmapResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<IReadOnlyList<int>> Grid);

/// <summary>每监控目录最近 24h 归档活动 sparkline（GET /dashboard/watch-folder-activity）</summary>
/// <remarks>
/// items 数组按 WatchFolder.Priority 升序；每项 series 长度固定 24（按小时桶，index 0 = 24h 前那个整点，index 23 = 当前整点）。
/// MediaItem 与 WatchFolder 的关联走「SourcePath 以 WatchFolder.Path 开头」启发式匹配（最长前缀匹配优先）。
/// </remarks>
public sealed record DashboardWatchFolderActivityResponse(IReadOnlyList<WatchFolderActivityEntry> Items);

public sealed record WatchFolderActivityEntry(
    long FolderId,
    string Path,
    string? Alias,
    bool Enabled,
    int Total,
    IReadOnlyList<int> Series);
