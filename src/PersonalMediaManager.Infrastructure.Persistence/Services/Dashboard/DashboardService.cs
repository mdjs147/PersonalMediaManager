using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Dashboard;
using PersonalMediaManager.Application.Services.Dashboard;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Dashboard;

/// <summary>仪表盘聚合实现（D7.7）</summary>
/// <remarks>
/// 多个聚合查询顺序 await 同一 DbContext（§0.5 红线禁止 Task.WhenAll 共享 DbContext）。
/// today 截断点 = 调用方时区当天 00:00 转 UTC；用 IClock 注入避免单测靠系统时钟。
/// service.running 永远 true（被调到说明服务在跑），uptimeSeconds 走 Process.StartTime。
/// </remarks>
internal sealed class DashboardService : IDashboardService
{
    private const int RecentDefaultLimit = 20;
    private const int RecentMaxLimit = 100;
    private const int TasksDefaultWindowHours = 24;
    private const int TasksMaxWindowHours = 168;  // 7 天上限
    private const int TasksDefaultLimit = 50;
    private const int TasksMaxLimit = 200;

    // 口径单一事实来源在 Domain.MediaItemStatusGroups，与「处理队列」列表共用，避免两处漂移
    private static readonly MediaItemStatus[] RunningStatuses = MediaItemStatusGroups.Running;

    private static readonly MediaItemStatus[] PendingStatuses = MediaItemStatusGroups.Pending;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public DashboardService(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task<DashboardStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        DateTimeOffset todayStart = new(_clock.UtcNow.Date, TimeSpan.Zero);
        IQueryable<MediaItem> items = db.MediaItems.AsNoTracking();

        // today
        int todayProcessed = await items.CountAsync(m => m.Status == MediaItemStatus.Completed && m.UpdatedAt >= todayStart, ct);
        int todaySkipped = await items.CountAsync(m => m.Status == MediaItemStatus.Skipped && m.UpdatedAt >= todayStart, ct);
        int todayFailed = await items.CountAsync(m => m.Status == MediaItemStatus.Failed && m.UpdatedAt >= todayStart, ct);
        int todayReview = await items.CountAsync(m => m.Status == MediaItemStatus.AwaitingReview && m.UpdatedAt >= todayStart, ct);

        // total
        int totalProcessed = await items.CountAsync(m => m.Status == MediaItemStatus.Completed, ct);
        int totalSkipped = await items.CountAsync(m => m.Status == MediaItemStatus.Skipped, ct);
        int totalFailed = await items.CountAsync(m => m.Status == MediaItemStatus.Failed, ct);

        // queue
        int queueReview = await items.CountAsync(m => m.Status == MediaItemStatus.AwaitingReview, ct);
        int queueRunning = await items.CountAsync(m => RunningStatuses.Contains(m.Status), ct);
        int queuePending = await items.CountAsync(m => PendingStatuses.Contains(m.Status), ct);

        // parseSource：仅命中规则/AI 的统计
        int psRule = await items.CountAsync(m => m.ParseSource == Domain.Enums.ParseSource.Rule, ct);
        int psAi = await items.CountAsync(m => m.ParseSource == Domain.Enums.ParseSource.Ai, ct);
        int psHybrid = await items.CountAsync(m => m.ParseSource == Domain.Enums.ParseSource.Hybrid, ct);

        long uptime = Math.Max(0, (long)(DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds);

        return new DashboardStats(
            new DashboardTodayBucket(todayProcessed, todaySkipped, todayFailed, todayReview),
            new DashboardTotalBucket(totalProcessed, totalSkipped, totalFailed),
            new DashboardQueueBucket(queueReview, queueRunning, queuePending),
            new DashboardParseSourceBucket(psRule, psAi, psHybrid),
            new DashboardServiceBucket(Running: true, UptimeSeconds: uptime));
    }

    public async Task<DashboardTasksResponse> GetTasksAsync(int windowHours, int limit, CancellationToken ct = default)
    {
        // 入参规范化（参考 GetRecentAsync 风格：宽松接收 + 上下限夹紧）
        if (windowHours <= 0) windowHours = TasksDefaultWindowHours;
        if (windowHours > TasksMaxWindowHours) windowHours = TasksMaxWindowHours;
        if (limit <= 0) limit = TasksDefaultLimit;
        if (limit > TasksMaxLimit) limit = TasksMaxLimit;

        DateTimeOffset cutoff = _clock.UtcNow.AddHours(-windowHours);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<ScheduledTaskRunEntry> items = await db.AuditScheduledTaskRuns.AsNoTracking()
            .Where(r => r.StartedAt >= cutoff)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .Select(r => new ScheduledTaskRunEntry(
                r.Id, r.JobKey, r.FireInstanceId,
                r.StartedAt, r.FinishedAt, r.DurationMs,
                r.Outcome, r.ProcessedCount, r.ErrorMessage, r.DetailJson))
            .ToListAsync(ct);

        return new DashboardTasksResponse(items);
    }

    public async Task<DashboardRecentList> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        if (limit <= 0) limit = RecentDefaultLimit;
        if (limit > RecentMaxLimit) limit = RecentMaxLimit;

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 左联 CategoryDefinition 拿分类名
        var rows = await (
            from m in db.MediaItems.AsNoTracking()
            orderby m.UpdatedAt descending
            join c in db.CategoryDefinitions.AsNoTracking() on m.CategoryId equals c.Id into gj
            from c in gj.DefaultIfEmpty()
            select new
            {
                m.Id, m.FileName, m.Status, m.ParsedInfo, m.TargetPath, m.ParseSource,
                m.ArchivedAt, m.UpdatedAt,
                CategoryName = c == null ? null : c.Name,
            }
        ).Take(limit).ToListAsync(ct);

        List<DashboardRecentItem> items = new(rows.Count);
        foreach (var r in rows)
        {
            (string? title, int? year) = ExtractTitleYear(r.ParsedInfo);
            items.Add(new DashboardRecentItem(
                r.Id, r.FileName, r.Status, title, year, r.CategoryName,
                r.TargetPath, r.ParseSource, r.ArchivedAt, r.UpdatedAt));
        }

        return new DashboardRecentList(items);
    }

    public async Task<DashboardHeatmapResponse> GetHeatmapAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        DateTimeOffset nowUtc = _clock.UtcNow;
        DateTimeOffset effectiveTo = to ?? nowUtc;
        DateTimeOffset effectiveFrom = from ?? effectiveTo.AddDays(-7);

        // 上限 90 天窗口防一次性扫全表；超过则收口到 effectiveTo - 90d
        DateTimeOffset windowFloor = effectiveTo.AddDays(-90);
        if (effectiveFrom < windowFloor) effectiveFrom = windowFloor;
        if (effectiveFrom > effectiveTo) effectiveFrom = effectiveTo;

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 仅投影 ArchivedAt，避免拉整行（SQLite 也能命中 IX_Media_Item_ArchivedAt 如有）
        List<DateTimeOffset> archivedAts = await db.MediaItems.AsNoTracking()
            .Where(m => m.ArchivedAt != null && m.ArchivedAt >= effectiveFrom && m.ArchivedAt <= effectiveTo)
            .Select(m => m.ArchivedAt!.Value)
            .ToListAsync(ct);

        // 7 行 × 24 列网格初始化为 0
        int[][] grid = new int[7][];
        for (int d = 0; d < 7; d++) grid[d] = new int[24];

        foreach (DateTimeOffset ts in archivedAts)
        {
            DateTimeOffset utc = ts.ToUniversalTime();
            int day = (int)utc.DayOfWeek;  // Sunday=0..Saturday=6
            int hour = utc.Hour;           // 0..23
            grid[day][hour]++;
        }

        return new DashboardHeatmapResponse(
            effectiveFrom, effectiveTo,
            grid.Select(row => (IReadOnlyList<int>)row.ToList()).ToList());
    }

    public async Task<DashboardWatchFolderActivityResponse> GetWatchFolderActivityAsync(CancellationToken ct = default)
    {
        DateTimeOffset nowUtc = _clock.UtcNow;
        // 当前整点向上对齐：例如 14:37 → "13 整点 ~ 14 整点" 算 index 23
        // 桶定义：index i 覆盖 [hourEnd - (24-i) hours, hourEnd - (23-i) hours)
        DateTimeOffset hourEnd = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        DateTimeOffset windowStart = hourEnd.AddHours(-24);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        List<WatchFolder> folders = await db.WatchFolders.AsNoTracking()
            .OrderBy(f => f.Priority).ThenBy(f => f.Id)
            .ToListAsync(ct);

        // 按 ArchivedAt 拉 24h 内的归档项（投影 SourcePath + ArchivedAt 即可）
        var rows = await db.MediaItems.AsNoTracking()
            .Where(m => m.ArchivedAt != null && m.ArchivedAt >= windowStart && m.ArchivedAt < hourEnd)
            .Select(m => new { m.SourcePath, m.ArchivedAt })
            .ToListAsync(ct);

        // 预排序按 Path 长度倒序：实现「最长前缀匹配优先」（嵌套目录场景子目录先匹配）
        List<WatchFolder> sortedByPathLen = folders
            .OrderByDescending(f => f.Path?.Length ?? 0)
            .ToList();

        Dictionary<long, int[]> perFolder = folders.ToDictionary(f => f.Id, _ => new int[24]);
        Dictionary<long, int> perFolderTotal = folders.ToDictionary(f => f.Id, _ => 0);

        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.SourcePath)) continue;
            WatchFolder? owner = sortedByPathLen.FirstOrDefault(f =>
                !string.IsNullOrEmpty(f.Path) &&
                r.SourcePath.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));
            if (owner is null) continue;

            DateTimeOffset utc = r.ArchivedAt!.Value.ToUniversalTime();
            int bucketIndex = (int)Math.Floor((utc - windowStart).TotalHours);
            if (bucketIndex < 0 || bucketIndex >= 24) continue;
            perFolder[owner.Id][bucketIndex]++;
            perFolderTotal[owner.Id]++;
        }

        List<WatchFolderActivityEntry> items = folders.Select(f => new WatchFolderActivityEntry(
            f.Id, f.Path ?? string.Empty, f.Alias, f.Enabled,
            perFolderTotal[f.Id], perFolder[f.Id].ToList())).ToList();

        return new DashboardWatchFolderActivityResponse(items);
    }

    /// <summary>从 ParsedInfo JSON 解析 title / year（损坏返回 null/null）</summary>
    internal static (string? title, int? year) ExtractTitleYear(string? parsedInfo)
    {
        if (string.IsNullOrWhiteSpace(parsedInfo)) return (null, null);
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(parsedInfo);
            string? title = doc.RootElement.TryGetProperty("title", out System.Text.Json.JsonElement t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString() : null;
            int? year = null;
            if (doc.RootElement.TryGetProperty("year", out System.Text.Json.JsonElement y))
            {
                if (y.ValueKind == System.Text.Json.JsonValueKind.Number && y.TryGetInt32(out int yv)) year = yv;
                else if (y.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(y.GetString(), out int ys)) year = ys;
            }
            return (title, year);
        }
        catch (System.Text.Json.JsonException) { return (null, null); }
    }
}
