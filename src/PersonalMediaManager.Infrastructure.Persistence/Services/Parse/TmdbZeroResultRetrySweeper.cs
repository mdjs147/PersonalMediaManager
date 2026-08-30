using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>ITmdbZeroResultRetrySweeper 实现：扫描 TMDB 未收录待确认项并重新排队</summary>
/// <remarks>
/// 筛选口径（四个条件缺一不可）：
///   · Status=AwaitingReview 且 ReviewReason=TmdbZeroResult——只碰「零结果」这一类；
///   · AiInvolved=false——即「规则高置信 + 多查询全零、被判定 TMDB 未收录」的跳 AI 记录；
///     AI 参与过的零结果（标题已被 AI 清洗仍搜不到）重投会再烧一遍 AI，不自动重投；
///   · UpdatedAt 距今 ≥ 20 小时——配合 Job 的 6 小时周期实现「每天最多重投一次」，
///     用户在审核页动过该记录（改字段 / 换候选）也会顺延一天，天然避让人工正在处理的条目；
///   · CreatedAt 在窗口天数内（Parse_ZeroResultRetryWindowDays，默认 14 天）——窗口耗尽视为
///     TMDB 不会收录（命名差异 / 冷门内容），停留人工队列不再打扰。
/// 重投 = Transition(Queued) + 清错误 + 重新入队走全管线：TMDB 已收录则直查自动归档；
/// 仍未收录则再次被「高置信全零跳 AI」拦回 AwaitingReview 等下一天——循环内永不烧 AI。
/// 入队失败（channel 异常）时记录已转 Queued：FullScanJob 周期补扫会重新发现源文件兜底入队，
/// 与 History.Rescan 的失败语义一致，不做本地回滚。
/// </remarks>
internal sealed class TmdbZeroResultRetrySweeper : ITmdbZeroResultRetrySweeper
{
    /// <summary>自动重试总开关键（bool，默认开；GeneralSettingsService.KnownSettings 同名）</summary>
    internal const string AutoRetryKey = "Parse_ZeroResultAutoRetry";
    /// <summary>重试窗口天数键（int，默认 14，钳位 [1,90]；GeneralSettingsService.KnownSettings 同名）</summary>
    internal const string RetryWindowDaysKey = "Parse_ZeroResultRetryWindowDays";

    private const int DefaultWindowDays = 14;
    private const int MinIntervalHours = 20;
    private const int MaxBatchPerSweep = 50;

    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPendingFileQueue _queue;
    private readonly ILogger<TmdbZeroResultRetrySweeper> _logger;

    public TmdbZeroResultRetrySweeper(
        IDbContextFactory<PmmDbContext> dbFactory,
        IPendingFileQueue queue,
        ILogger<TmdbZeroResultRetrySweeper> logger)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        if (!await ReadAutoRetryEnabledAsync(db, ct)) return 0;
        int windowDays = await ReadWindowDaysAsync(db, ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset staleBefore = now.AddHours(-MinIntervalHours);
        DateTimeOffset windowStart = now.AddDays(-windowDays);

        List<MediaItem> due = await db.MediaItems
            .Where(m => m.Status == MediaItemStatus.AwaitingReview
                     && m.ReviewReason == ReviewReason.TmdbZeroResult
                     && !m.AiInvolved
                     && m.CreatedAt >= windowStart
                     && m.UpdatedAt <= staleBefore)
            .OrderBy(m => m.UpdatedAt)
            .Take(MaxBatchPerSweep)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        IReadOnlyList<(long Id, string Path)> watchRoots = await LoadWatchFolderRootsAsync(db, ct);

        int requeued = 0;
        foreach (MediaItem item in due)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(item.SourcePath))
            {
                // 源文件已不在（被删 / 移走）：跳过本轮，交由 FileMissing 巡检 / 人工处置，不在 Job 里做删改决策
                continue;
            }

            int ageDays = Math.Max(1, (int)Math.Ceiling((now - item.CreatedAt).TotalDays));
            item.AppendStep(MediaItemStatus.AwaitingReview, now, durMs: 0, JsonSerializer.Serialize(new
            {
                decision = $"TMDB 未收录每日自动重试（入库第 {ageDays} 天 / 窗口 {windowDays} 天）→ 重新排队全流程",
                autoRetry = true,
            }, StepJsonOptions));
            item.Transition(MediaItemStatus.Queued);
            item.ClearError();
            await db.SaveChangesAsync(ct);

            long watchFolderId = ResolveWatchFolderIdInMemory(item.SourcePath, watchRoots);
            try
            {
                await _queue.EnqueueAsync(new PendingFileItem(item.SourcePath, watchFolderId, PendingFileSource.Manual), ct);
                requeued++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TMDB 未收录自动重试入队失败（记录已转 Queued，待全量扫描兜底）：MediaItemId={Id}", item.Id);
            }
        }

        if (requeued > 0)
        {
            _logger.LogInformation("TMDB 未收录自动重试：本轮重投 {Count} 条（窗口 {WindowDays} 天）", requeued, windowDays);
        }
        return requeued;
    }

    /// <summary>读总开关：缺行 / 空值按默认开；仅显式 "false"/"0" 关闭</summary>
    private static async Task<bool> ReadAutoRetryEnabledAsync(PmmDbContext db, CancellationToken ct)
    {
        string? raw = (await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AutoRetryKey, ct))?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        return !(string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw.Trim() == "0");
    }

    /// <summary>读窗口天数：缺行 / 非法值回默认 14，钳位 [1,90]</summary>
    private static async Task<int> ReadWindowDaysAsync(PmmDbContext db, CancellationToken ct)
    {
        string? raw = (await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == RetryWindowDaysKey, ct))?.Value;
        if (!int.TryParse(raw, out int days)) return DefaultWindowDays;
        return Math.Clamp(days, 1, 90);
    }

    /// <summary>一次性加载监控目录 (Id, Path) 投影（与 HistoryService 同构，供循环外预载）</summary>
    private static async Task<IReadOnlyList<(long Id, string Path)>> LoadWatchFolderRootsAsync(PmmDbContext db, CancellationToken ct)
    {
        var rows = await db.WatchFolders.AsNoTracking()
            .Where(w => w.Path != null && w.Path != "")
            .Select(w => new { w.Id, w.Path })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, Path: r.Path!)).ToList();
    }

    /// <summary>纯内存「最长目录前缀」匹配（含目录段边界；找不到返回 0，管线退化为单段父目录上下文）</summary>
    private static long ResolveWatchFolderIdInMemory(string sourcePath, IReadOnlyList<(long Id, string Path)> roots)
    {
        long bestId = 0;
        int bestLen = -1;
        foreach ((long id, string path) in roots)
        {
            string root = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length == 0) continue;
            bool hit = sourcePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || sourcePath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (hit && root.Length > bestLen)
            {
                bestId = id;
                bestLen = root.Length;
            }
        }
        return bestId;
    }
}
