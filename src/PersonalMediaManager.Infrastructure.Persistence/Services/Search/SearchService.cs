using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Search;
using PersonalMediaManager.Application.Services.Search;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Search;

/// <summary>统一全局搜索服务实现 — 历史 / 媒体库 / 待确认三域聚合</summary>
/// <remarks>
/// 单 DbContext 串行查三段（CLAUDE.md §八红线：禁同 context Task.WhenAll）。各域口径：
/// - History：终态集合（MediaItemStatusGroups.Terminal）+ Like(FileName,p)||Like(ParsedInfo,p)，
///   与处理历史页 keyword 同口径（ParsedInfo 整段 JSON LIKE，会命中 JSON key 名噪声——保持既有行为一致）；
///   Count + OrderByDescending(UpdatedAt).Take(N)，Title/Year 由 ParsedInfo.FromJson 平铺。
/// - Review：限定 AwaitingReview + 同 LIKE；Count + OrderByDescending(Id).Take(N)。
/// - Library：SQL 查 MediaWorks Like(Title,p)||(OriginalTitle!=null && Like(OriginalTitle,p))，
///   仅保留库内已归档作品键（有 Completed 文件的 (TmdbId,MediaType)）；topN + 按键 FileCount，
///   HasPoster 仅对返回 topN 算 File.Exists（不全表 IO）。Library 召回依赖 TMDB 富化标题，不回退搜 ParsedInfo。
/// SQLite LIKE 默认 ASCII 大小写不敏感；中文按字节匹配。空 / 纯空白关键词直接返回空结果不触发任何查询。
/// </remarks>
internal sealed class SearchService : ISearchService
{
    private const int KeywordMaxLength = 100;
    private const int MinLimitPerGroup = 1;
    private const int MaxLimitPerGroup = 20;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly AppPaths _paths;

    public SearchService(IDbContextFactory<PmmDbContext> dbFactory, AppPaths paths)
    {
        _dbFactory = dbFactory;
        _paths = paths;
    }

    public async Task<GlobalSearchResponse> ListAsync(GlobalSearchQuery query, CancellationToken ct = default)
    {
        // 关键词归一化：空 / 纯空白短路返回空结果（前端空串也已短路，此处兜底）
        string kw = (query.Q ?? string.Empty).Trim();
        if (kw.Length == 0)
        {
            return new GlobalSearchResponse(
                string.Empty,
                Array.Empty<HistoryHit>(), Array.Empty<LibraryHit>(), Array.Empty<ReviewHit>(),
                new SearchCounts(0, 0, 0));
        }
        if (kw.Length > KeywordMaxLength) kw = kw[..KeywordMaxLength];
        string pattern = $"%{kw}%";

        int limit = Math.Clamp(query.LimitPerGroup, MinLimitPerGroup, MaxLimitPerGroup);
        (bool wantHistory, bool wantLibrary, bool wantReview) = ParseScopes(query.Scopes);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // ---------- 1. History（终态 + 文件名 / ParsedInfo LIKE） ----------
        List<HistoryHit> historyHits = new();
        int historyCount = 0;
        if (wantHistory)
        {
            IQueryable<MediaItem> hq = db.MediaItems.AsNoTracking()
                .Where(m => MediaItemStatusGroups.Terminal.Contains(m.Status))
                .Where(m => EF.Functions.Like(m.FileName, pattern)
                    || (m.ParsedInfo != null && EF.Functions.Like(m.ParsedInfo, pattern)));

            historyCount = await hq.CountAsync(ct);

            var rows = await hq
                .OrderByDescending(m => m.UpdatedAt)
                .Take(limit)
                .Select(m => new { m.Id, m.FileName, m.ParsedInfo, m.Status, m.TmdbId, m.TmdbMediaType })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                ParsedInfo? pi = ParsedInfo.FromJson(r.ParsedInfo);
                historyHits.Add(new HistoryHit(
                    r.Id, r.FileName, pi?.Title, pi?.Year, r.Status, r.TmdbId, r.TmdbMediaType));
            }
        }

        // ---------- 2. Review（限定 AwaitingReview + 同 LIKE） ----------
        List<ReviewHit> reviewHits = new();
        int reviewCount = 0;
        if (wantReview)
        {
            IQueryable<MediaItem> rq = db.MediaItems.AsNoTracking()
                .Where(m => m.Status == MediaItemStatus.AwaitingReview)
                .Where(m => EF.Functions.Like(m.FileName, pattern)
                    || (m.ParsedInfo != null && EF.Functions.Like(m.ParsedInfo, pattern)));

            reviewCount = await rq.CountAsync(ct);

            var rows = await rq
                .OrderByDescending(m => m.Id)
                .Take(limit)
                .Select(m => new { m.Id, m.FileName, m.ParsedInfo })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                ParsedInfo? pi = ParsedInfo.FromJson(r.ParsedInfo);
                reviewHits.Add(new ReviewHit(r.Id, r.FileName, pi?.Title, pi?.Year));
            }
        }

        // ---------- 3. Library（MediaWork 标题 / 原文名 LIKE，限库内已归档键） ----------
        List<LibraryHit> libraryHits = new();
        int libraryCount = 0;
        if (wantLibrary)
        {
            // 库内已归档作品键（有 Completed 文件）+ 每键文件数：一次分组查
            var libKeyCounts = (await db.MediaItems.AsNoTracking()
                    .Where(m => m.Status == MediaItemStatus.Completed && m.TmdbId != null && m.TmdbMediaType != null)
                    .GroupBy(m => new { TmdbId = m.TmdbId!.Value, MediaType = m.TmdbMediaType! })
                    .Select(g => new { g.Key.TmdbId, g.Key.MediaType, FileCount = g.Count() })
                    .ToListAsync(ct))
                .ToDictionary(x => (x.TmdbId, x.MediaType), x => x.FileCount);

            if (libKeyCounts.Count > 0)
            {
                // 标题 / 原文名命中的作品键（SQL LIKE），内存与库内键求交保证「只搜已归档」
                var matchedWorks = await db.MediaWorks.AsNoTracking()
                    .Where(w => EF.Functions.Like(w.Title!, pattern)
                        || (w.OriginalTitle != null && EF.Functions.Like(w.OriginalTitle, pattern)))
                    .Select(w => new { w.TmdbId, w.MediaType, w.Title, w.Year })
                    .ToListAsync(ct);

                var inLibrary = matchedWorks
                    .Where(w => libKeyCounts.ContainsKey((w.TmdbId, w.MediaType)))
                    // 同一作品键去重（理论上 MediaWork 对 TmdbId+MediaType 唯一，去重兜底历史脏数据）
                    .GroupBy(w => (w.TmdbId, w.MediaType))
                    .Select(g => g.First())
                    .ToList();

                libraryCount = inLibrary.Count;

                // topN：按文件数倒序（库内占比更重者优先），同数按 TmdbId 倒序稳定
                var topN = inLibrary
                    .OrderByDescending(w => libKeyCounts[(w.TmdbId, w.MediaType)])
                    .ThenByDescending(w => w.TmdbId)
                    .Take(limit)
                    .ToList();

                foreach (var w in topN)
                {
                    bool hasPoster = File.Exists(Path.Combine(_paths.PostersDir, $"{w.TmdbId}.jpg"));
                    libraryHits.Add(new LibraryHit(
                        w.TmdbId, w.MediaType, w.Title, w.Year, hasPoster,
                        libKeyCounts[(w.TmdbId, w.MediaType)]));
                }
            }
        }

        return new GlobalSearchResponse(
            kw, historyHits, libraryHits, reviewHits,
            new SearchCounts(historyCount, libraryCount, reviewCount));
    }

    /// <summary>解析 Scopes（逗号分隔）；缺省 / 空 / 全空白 → 三域全开</summary>
    /// <remarks>大小写不敏感，未知 token 忽略；显式给值但无任何已知域时退化为不查任何域（返回空结果）。</remarks>
    private static (bool History, bool Library, bool Review) ParseScopes(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes)) return (true, true, true);

        bool history = false, library = false, review = false;
        foreach (string raw in scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("history", StringComparison.OrdinalIgnoreCase)) history = true;
            else if (raw.Equals("library", StringComparison.OrdinalIgnoreCase)) library = true;
            else if (raw.Equals("review", StringComparison.OrdinalIgnoreCase)) review = true;
        }
        return (history, library, review);
    }
}
