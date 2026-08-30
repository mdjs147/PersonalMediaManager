using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Statistics;
using PersonalMediaManager.Application.Services.Statistics;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Statistics;

/// <summary>统计分析聚合实现</summary>
/// <remarks>
/// 多个聚合查询顺序 await 同一 DbContext（§八红线禁 Task.WhenAll 共享 DbContext）。
/// 作品标量维度（电影/剧集数、年代、评分、国家、分类）全量载入 inLib 内存聚合（库规模可控，沿用 LibraryService.GetFacets 范式）；
/// 类型(genre) 走连接表 DB 聚合；文件级维度（文件数 / 总容量 / 按月趋势 / 存储 Top）走 MediaItem 查询。
/// 「库内作品」口径 = 有 Completed 文件的 MediaWork，与媒体库视图一致。
/// </remarks>
internal sealed class StatisticsService : IStatisticsService
{
    private const int TopN = 10;
    private const int CountryTopN = 8;
    private const int TrendMonths = 12;     // 1y 档 + all 空库兜底的月桶数
    private const int MaxTrendMonths = 36;  // all 档月桶上限（防超长库一次性铺满）

    // 评分直方图固定 5 桶顺序（空桶补零，保证柱图连续）
    private static readonly string[] RatingBucketLabels = ["<6", "6~7", "7~8", "8~9", "9~10"];

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;

    public StatisticsService(IDbContextFactory<PmmDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task<StatisticsOverview> GetOverviewAsync(string? range = null, CancellationToken ct = default)
    {
        string r = NormalizeRange(range);
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset? from = ResolveFrom(r, now);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 整页按归档时间窗过滤：all → 不过滤（全量库构成）；其余 → 仅窗口内归档的 Completed 文件
        // 库构成各维度（summary/inLib/storage）皆基于此 completed，过滤一处即全页跟随窗口
        IQueryable<MediaItem> completed = db.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.Completed);
        if (from is not null)
            completed = completed.Where(m => m.ArchivedAt != null && m.ArchivedAt >= from);

        // 文件级：文件数 + 总容量（SumAsync 空集合用 (long?)…?? 0 兜底）
        int fileCount = await completed.CountAsync(ct);
        long totalSize = await completed.SumAsync(m => (long?)m.FileSize, ct) ?? 0L;

        // 库内作品键（有已完成文件，distinct tmdbId+type）
        HashSet<(int, string)> libKeys = (await completed
            .Where(m => m.TmdbId != null && m.TmdbMediaType != null)
            .Select(m => new { TmdbId = m.TmdbId!.Value, MediaType = m.TmdbMediaType! })
            .Distinct().ToListAsync(ct))
            .Select(x => (x.TmdbId, x.MediaType)).ToHashSet();

        // 库内作品标量（内存聚合源）
        var allWorks = await db.MediaWorks.AsNoTracking()
            .Select(w => new { w.Id, w.TmdbId, w.MediaType, w.Title, w.Year, w.VoteAverage, w.OriginCountry, w.CategoryId })
            .ToListAsync(ct);
        var inLib = allWorks.Where(w => libKeys.Contains((w.TmdbId, w.MediaType))).ToList();

        int movieCount = inLib.Count(w => w.MediaType == "movie");
        int tvCount = inLib.Count(w => w.MediaType == "tv");

        // 评分（仅有效评分作品参与平均与直方图）
        List<double> rated = inLib
            .Where(w => w.VoteAverage.HasValue && w.VoteAverage.Value > 0)
            .Select(w => w.VoteAverage!.Value).ToList();
        double? avgRating = rated.Count > 0 ? Math.Round(rated.Average(), 1) : null;

        // 年份跨度
        List<int> years = inLib.Where(w => w.Year.HasValue).Select(w => w.Year!.Value).ToList();
        int? oldestYear = years.Count > 0 ? years.Min() : null;
        int? newestYear = years.Count > 0 ? years.Max() : null;

        StatisticsSummary summary = new(
            movieCount, tvCount, movieCount + tvCount,
            fileCount, totalSize, avgRating, rated.Count, oldestYear, newestYear);

        // 入库趋势（粒度随 range 自适应：30d/90d 按天，1y/all 按月）
        (string granularity, List<TrendPoint> trend) = await BuildTrendAsync(completed, r, now, ct);

        // 年代分布（按十年代分桶，升序）
        List<DecadeBucket> decadeDistribution = years
            .GroupBy(y => y / 10 * 10)
            .OrderBy(g => g.Key)
            .Select(g => new DecadeBucket(g.Key, g.Count()))
            .ToList();

        // 评分直方图（固定 5 桶，补零）
        Dictionary<string, int> ratingCounts = rated
            .GroupBy(RatingBucketOf).ToDictionary(g => g.Key, g => g.Count());
        List<RatingBucket> ratingHistogram = RatingBucketLabels
            .Select(l => new RatingBucket(l, ratingCounts.GetValueOrDefault(l, 0)))
            .ToList();

        // 类型 Top（连接表 DB 聚合 + 维度名映射）
        List<NamedCount> genreTop = await BuildGenreTopAsync(db, inLib.Select(w => w.Id).ToList(), ct);

        // 出品国家 Top（内存）
        List<NamedCount> countryTop = inLib
            .Where(w => w.OriginCountry != null)
            .SelectMany(w => w.OriginCountry!)
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Take(CountryTopN)
            .Select(g => new NamedCount(g.Key, g.Count()))
            .ToList();

        // 分类构成（内存 group + 名字映射）
        List<NamedCount> categoryDistribution = await BuildCategoryDistributionAsync(
            db, inLib.Where(w => w.CategoryId.HasValue).Select(w => w.CategoryId!.Value).ToList(), ct);

        // 存储 Top（按作品聚合 Completed 文件大小）
        var storageRaw = await completed
            .Where(m => m.TmdbId != null && m.TmdbMediaType != null)
            .GroupBy(m => new { Tmdb = m.TmdbId!.Value, Type = m.TmdbMediaType! })
            .Select(g => new { g.Key.Tmdb, g.Key.Type, Size = g.Sum(x => x.FileSize) })
            .OrderByDescending(x => x.Size).Take(TopN).ToListAsync(ct);
        Dictionary<(int, string), (string? Title, int? Year)> titleMap = inLib
            .ToDictionary(w => (w.TmdbId, w.MediaType), w => (w.Title, w.Year));
        List<StorageWork> storageTop = storageRaw.Select(s =>
        {
            titleMap.TryGetValue((s.Tmdb, s.Type), out (string? Title, int? Year) info);
            return new StorageWork(s.Tmdb, s.Type, info.Title, info.Year, s.Size);
        }).ToList();

        // 识别方式构成 + AI / 人工参与度（与整页同基线 completed，分母复用 fileCount）
        IdentificationStats identification = await BuildIdentificationAsync(db, completed, fileCount, ct);

        return new StatisticsOverview(
            r, granularity, summary, trend, decadeDistribution, ratingHistogram,
            genreTop, countryTop, categoryDistribution, storageTop, identification);
    }

    /// <summary>归一化时间范围：30d / 90d / 1y / all（空或未知 → all）</summary>
    private static string NormalizeRange(string? range)
    {
        string r = (range ?? "all").Trim().ToLowerInvariant();
        return r is "30d" or "90d" or "1y" or "all" ? r : "all";
    }

    /// <summary>库构成过滤下界：all → null（不过滤）；30d/90d 滚动天；1y 近 12 月起点</summary>
    private static DateTimeOffset? ResolveFrom(string r, DateTimeOffset now) => r switch
    {
        "30d" => now.AddDays(-30),
        "90d" => now.AddDays(-90),
        "1y" => FirstOfMonth(now).AddMonths(-(TrendMonths - 1)),
        _ => null,
    };

    private static DateTimeOffset FirstOfMonth(DateTimeOffset t) => new(t.Year, t.Month, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset FirstOfDay(DateTimeOffset t) => new(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>入库趋势：30d/90d 按天补零；1y 近 12 月、all 从最早归档月（上限 36）按月补零</summary>
    private async Task<(string Granularity, List<TrendPoint> Points)> BuildTrendAsync(
        IQueryable<MediaItem> completed, string r, DateTimeOffset now, CancellationToken ct)
    {
        if (r is "30d" or "90d")
        {
            int days = r == "30d" ? 30 : 90;
            DateTimeOffset start = FirstOfDay(now).AddDays(-(days - 1));
            List<string> keys = Enumerable.Range(0, days)
                .Select(i => start.AddDays(i).ToString("yyyy-MM-dd")).ToList();
            Dictionary<string, int> counts = await BucketCountsAsync(completed, start, "yyyy-MM-dd", ct);
            return ("day", keys.Select(k => new TrendPoint(k, counts.GetValueOrDefault(k, 0))).ToList());
        }

        DateTimeOffset firstThis = FirstOfMonth(now);
        DateTimeOffset monthStart;
        int monthCount;
        if (r == "1y")
        {
            monthStart = firstThis.AddMonths(-(TrendMonths - 1));
            monthCount = TrendMonths;
        }
        else // all：从最早归档月到当前月，上限 MaxTrendMonths；空库回退近 12 月
        {
            DateTimeOffset? earliest = await completed.Select(m => m.ArchivedAt).MinAsync(ct);
            if (earliest is null)
            {
                monthStart = firstThis.AddMonths(-(TrendMonths - 1));
                monthCount = TrendMonths;
            }
            else
            {
                DateTimeOffset em = earliest.Value.ToUniversalTime();
                DateTimeOffset earliestMonth = new(em.Year, em.Month, 1, 0, 0, 0, TimeSpan.Zero);
                int span = ((firstThis.Year - earliestMonth.Year) * 12) + (firstThis.Month - earliestMonth.Month) + 1;
                monthCount = Math.Clamp(span, 1, MaxTrendMonths);
                monthStart = firstThis.AddMonths(-(monthCount - 1));
            }
        }

        List<string> mkeys = Enumerable.Range(0, monthCount)
            .Select(i => monthStart.AddMonths(i).ToString("yyyy-MM")).ToList();
        Dictionary<string, int> mcounts = await BucketCountsAsync(completed, monthStart, "yyyy-MM", ct);
        return ("month", mkeys.Select(k => new TrendPoint(k, mcounts.GetValueOrDefault(k, 0))).ToList());
    }

    /// <summary>投影窗口内 ArchivedAt 并按给定格式（天/月）内存分桶计数</summary>
    private static async Task<Dictionary<string, int>> BucketCountsAsync(
        IQueryable<MediaItem> completed, DateTimeOffset start, string fmt, CancellationToken ct)
    {
        List<DateTimeOffset> stamps = await completed
            .Where(m => m.ArchivedAt != null && m.ArchivedAt >= start)
            .Select(m => m.ArchivedAt!.Value)
            .ToListAsync(ct);
        return stamps
            .GroupBy(d => d.ToUniversalTime().ToString(fmt))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>类型(genre) Top N：连接表分组取计数 → 维度表取名（沿用 LibraryService.FacetJoin 范式）</summary>
    private static async Task<List<NamedCount>> BuildGenreTopAsync(PmmDbContext db, List<long> workIds, CancellationToken ct)
    {
        if (workIds.Count == 0) return [];

        var counts = await db.MediaWorkGenres.AsNoTracking()
            .Where(x => workIds.Contains(x.WorkId))
            .GroupBy(x => x.GenreId)
            .Select(g => new { GenreId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(TopN)
            .ToListAsync(ct);
        if (counts.Count == 0) return [];

        List<int> ids = counts.Select(c => c.GenreId).ToList();
        Dictionary<int, string> names = (await db.MediaGenres.AsNoTracking()
            .Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .ToDictionary(n => n.Id, n => n.Name);

        return counts
            .Where(c => names.ContainsKey(c.GenreId))
            .Select(c => new NamedCount(names[c.GenreId], c.Count))
            .ToList();
    }

    /// <summary>分类构成：内存 group CategoryId → CategoryDefinition 取名（按计数降序）</summary>
    private static async Task<List<NamedCount>> BuildCategoryDistributionAsync(PmmDbContext db, List<long> categoryIds, CancellationToken ct)
    {
        if (categoryIds.Count == 0) return [];

        Dictionary<long, int> counts = categoryIds
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        List<long> ids = counts.Keys.ToList();
        Dictionary<long, string> names = (await db.CategoryDefinitions.AsNoTracking()
            .Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);

        return counts
            .Where(kv => names.ContainsKey(kv.Key))
            .Select(kv => new NamedCount(names[kv.Key], kv.Value))
            .OrderByDescending(n => n.Count)
            .ToList();
    }

    /// <summary>识别方式构成：ParseSource DB 端分组 + AiInvolved / 人工审核 EXISTS 计数</summary>
    /// <remarks>
    /// 三个查询顺序 await 同一 DbContext（§八红线禁并发共享）：
    /// - ParseSource 分组走 DB 端 GroupBy（TEXT 枚举列，null 归 OtherCount）；
    /// - ReviewInvolvedCount 用 db.ProcessSteps.Any(...) 让 EF 翻译成 EXISTS 相关子查询
    ///   （Stage 复用 MediaItemStatus 枚举，AwaitingReview 即「走过人工审核」的时间线痕迹）。
    /// </remarks>
    private static async Task<IdentificationStats> BuildIdentificationAsync(
        PmmDbContext db, IQueryable<MediaItem> completed, int total, CancellationToken ct)
    {
        var srcCounts = await completed
            .GroupBy(m => m.ParseSource)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int CountOf(ParseSource? s) => srcCounts.FirstOrDefault(x => x.Source == s)?.Count ?? 0;

        int aiInvolved = await completed.CountAsync(m => m.AiInvolved, ct);
        int reviewInvolved = await completed.CountAsync(
            m => db.ProcessSteps.Any(s => s.MediaItemId == m.Id && s.Stage == MediaItemStatus.AwaitingReview), ct);

        return new IdentificationStats(
            total,
            CountOf(ParseSource.Rule),
            CountOf(ParseSource.Ai),
            CountOf(ParseSource.Hybrid),
            CountOf(ParseSource.Manual),
            CountOf(null),
            aiInvolved,
            reviewInvolved);
    }

    /// <summary>评分值落桶：&lt;6 / 6~7 / 7~8 / 8~9 / 9~10</summary>
    private static string RatingBucketOf(double v)
        => v < 6 ? "<6" : v >= 9 ? "9~10" : $"{(int)Math.Floor(v)}~{(int)Math.Floor(v) + 1}";
}
