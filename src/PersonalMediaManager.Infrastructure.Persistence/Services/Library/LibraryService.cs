using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Library;
using PersonalMediaManager.Application.Services.Library;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Library;

/// <summary>媒体库服务 — 已归档作品浏览 + 富化关联 + 库内搜索</summary>
/// <remarks>
/// 作品全集来自 Completed 且有 TmdbId 的 MediaItem（内存按 TmdbId+MediaType 聚合，个人库规模可控）；
/// 富化字段 LEFT JOIN Media_Work；维度过滤走连接表 SQL EXISTS，年份/国家/语言在合并元数据后内存过滤。
/// 详情/分季打开时经 IWorkEnrichmentService 惰性富化（失败降级返回已有数据）。
/// </remarks>
internal sealed class LibraryService : ILibraryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 24;
    private const int KeywordMaxLength = 100;
    private const int FacetCap = 50;
    private const int RelatedCap = 12;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IWorkEnrichmentService _enrichment;
    private readonly IMediaAudioProbe _audioProbe;
    private readonly IMediaExtensionProvider _extensions;
    private readonly IFileIntakeService _intake;
    private readonly AppPaths _paths;
    private readonly ILogger<LibraryService> _logger;

    public LibraryService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IWorkEnrichmentService enrichment,
        IMediaAudioProbe audioProbe,
        IMediaExtensionProvider extensions,
        IFileIntakeService intake,
        AppPaths paths,
        ILogger<LibraryService> logger)
    {
        _dbFactory = dbFactory;
        _enrichment = enrichment;
        _audioProbe = audioProbe;
        _extensions = extensions;
        _intake = intake;
        _paths = paths;
        _logger = logger;
    }

    public async Task<LibraryListPage> ListAsync(LibraryListQuery query, CancellationToken ct = default)
    {
        int page = query.Page < 1 ? 1 : query.Page;
        int pageSize = query.PageSize <= 0 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        IQueryable<MediaItem> mq = db.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.Completed && m.TmdbId != null);
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            string t = query.Type.Trim().ToLowerInvariant();
            mq = mq.Where(m => m.TmdbMediaType == t);
        }
        if (query.CategoryId is not null)
            mq = mq.Where(m => m.CategoryId == query.CategoryId.Value);

        var rows = await mq
            .Select(m => new { m.TmdbId, MediaType = m.TmdbMediaType, m.ParsedInfo, m.CategoryId, m.ArchivedAt, m.FileMissing })
            .ToListAsync(ct);

        // 无界加载守护：个人库规模可控，故全量载入内存再 GroupBy/分页（规避 EF GroupBy 取代表行 + CJK LIKE 复杂度）；
        // 超软上限仅告警不阻断，便于发现异常膨胀后再评估改 SQL 侧聚合。
        const int softLimit = 20000;
        if (rows.Count > softLimit)
            _logger.LogWarning("媒体库已完成项 {Count} 条超过软上限 {Limit}，ListAsync 全量载入内存分页可能变慢", rows.Count, softLimit);

        var grouped = rows
            .GroupBy(r => new { TmdbId = r.TmdbId!.Value, Type = r.MediaType ?? string.Empty })
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.ArchivedAt).First();
                ParsedInfo? pi = ParsedInfo.FromJson(latest.ParsedInfo);
                return new
                {
                    g.Key.TmdbId,
                    MediaType = g.Key.Type,
                    ParsedTitle = pi?.Title,
                    ParsedYear = pi?.Year,
                    latest.CategoryId,
                    FileCount = g.Count(),
                    MissingFileCount = g.Count(x => x.FileMissing),
                    LatestArchivedAt = g.Max(x => x.ArchivedAt),
                };
            })
            .ToList();

        // 维度过滤（genre/person/company/network/keyword）→ SQL EXISTS 取命中作品键集合
        if (query.GenreId is not null || query.PersonId is not null || query.CompanyId is not null
            || query.NetworkId is not null || query.KeywordId is not null)
        {
            IQueryable<MediaWork> wq = db.MediaWorks.AsNoTracking();
            if (query.GenreId is int gid) wq = wq.Where(w => db.MediaWorkGenres.Any(x => x.WorkId == w.Id && x.GenreId == gid));
            if (query.PersonId is int pid) wq = wq.Where(w => db.MediaWorkCredits.Any(x => x.WorkId == w.Id && x.PersonId == pid));
            if (query.CompanyId is int cid) wq = wq.Where(w => db.MediaWorkCompanies.Any(x => x.WorkId == w.Id && x.CompanyId == cid));
            if (query.NetworkId is int nid) wq = wq.Where(w => db.MediaWorkNetworks.Any(x => x.WorkId == w.Id && x.NetworkId == nid));
            if (query.KeywordId is int kid) wq = wq.Where(w => db.MediaWorkKeywords.Any(x => x.WorkId == w.Id && x.KeywordId == kid));

            HashSet<(int, string)> matched = (await wq.Select(w => new { w.TmdbId, w.MediaType }).ToListAsync(ct))
                .Select(x => (x.TmdbId, x.MediaType))
                .ToHashSet();
            grouped = grouped.Where(g => matched.Contains((g.TmdbId, g.MediaType))).ToList();
        }

        if (grouped.Count == 0)
            return new LibraryListPage(Array.Empty<LibraryItemResponse>(), 0, page, pageSize);

        // 合并富化元数据（Media_Work 优先 → 元数据缓存 → 解析）
        List<int> tmdbIds = grouped.Select(g => g.TmdbId).Distinct().ToList();
        var workMeta = (await db.MediaWorks.AsNoTracking()
            .Where(w => tmdbIds.Contains(w.TmdbId))
            .Select(w => new { w.TmdbId, w.MediaType, w.Title, w.Year, w.VoteAverage, w.OriginalLanguage, w.OriginCountry })
            .ToListAsync(ct))
            .ToDictionary(w => (w.TmdbId, w.MediaType));
        var cacheMeta = (await db.TmdbMetadataCaches.AsNoTracking()
            .Where(c => tmdbIds.Contains(c.TmdbId))
            .Select(c => new { c.TmdbId, c.MediaType, c.Title, c.Year, c.OriginalLanguage, c.OriginCountry })
            .ToListAsync(ct))
            .ToDictionary(c => (c.TmdbId, c.MediaType));

        var merged = grouped.Select(g =>
        {
            workMeta.TryGetValue((g.TmdbId, g.MediaType), out var w);
            cacheMeta.TryGetValue((g.TmdbId, g.MediaType), out var c);
            return new
            {
                g.TmdbId,
                g.MediaType,
                g.CategoryId,
                g.FileCount,
                g.MissingFileCount,
                g.LatestArchivedAt,
                Title = w?.Title ?? c?.Title ?? g.ParsedTitle,
                Year = w?.Year ?? c?.Year ?? g.ParsedYear,
                Rating = w?.VoteAverage,
                Language = w?.OriginalLanguage ?? c?.OriginalLanguage,
                Country = w?.OriginCountry ?? c?.OriginCountry,
            };
        });

        // 内存过滤：年份/国家/语言/关键词
        if (query.YearFrom is int yf) merged = merged.Where(x => x.Year != null && x.Year >= yf);
        if (query.YearTo is int yt) merged = merged.Where(x => x.Year != null && x.Year <= yt);
        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            string lg = query.Language.Trim();
            merged = merged.Where(x => string.Equals(x.Language, lg, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            string co = query.Country.Trim().ToUpperInvariant();
            merged = merged.Where(x => x.Country != null && x.Country.Contains(co));
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string kw = query.Keyword.Trim();
            if (kw.Length > KeywordMaxLength) kw = kw[..KeywordMaxLength];
            merged = merged.Where(x => x.Title != null && x.Title.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var list = merged.ToList();
        string sort = (query.Sort ?? "recent").Trim().ToLowerInvariant();
        list = sort switch
        {
            "year" => list.OrderByDescending(x => x.Year ?? int.MinValue).ThenByDescending(x => x.TmdbId).ToList(),
            "rating" => list.OrderByDescending(x => x.Rating ?? double.MinValue).ThenByDescending(x => x.LatestArchivedAt).ToList(),
            "title" => list.OrderBy(x => x.Title ?? "￿", StringComparer.CurrentCulture).ToList(),
            _ => list.OrderByDescending(x => x.LatestArchivedAt).ThenByDescending(x => x.TmdbId).ToList(),
        };

        int total = list.Count;
        var pageItems = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // 当前页增强：分类名 + 类型名（前 3）+ 海报存在
        List<int> pageTmdbIds = pageItems.Select(x => x.TmdbId).Distinct().ToList();
        List<long> catIds = pageItems.Where(x => x.CategoryId != null).Select(x => x.CategoryId!.Value).Distinct().ToList();
        Dictionary<long, string> catMap = catIds.Count == 0
            ? new Dictionary<long, string>()
            : (await db.CategoryDefinitions.AsNoTracking().Where(c => catIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);

        // 类型名：Media_Work → WorkGenre → Genre，按页作品聚合
        var pageWorks = await db.MediaWorks.AsNoTracking()
            .Where(w => pageTmdbIds.Contains(w.TmdbId))
            .Select(w => new { w.Id, w.TmdbId, w.MediaType })
            .ToListAsync(ct);
        Dictionary<long, (int TmdbId, string MediaType)> workKeyById =
            pageWorks.ToDictionary(w => w.Id, w => (w.TmdbId, w.MediaType));
        List<long> pageWorkIds = pageWorks.Select(w => w.Id).ToList();
        List<(long WorkId, string Name)> genreRows = pageWorkIds.Count == 0
            ? new List<(long WorkId, string Name)>()
            : (await db.MediaWorkGenres.AsNoTracking().Where(g => pageWorkIds.Contains(g.WorkId))
                .Join(db.MediaGenres.AsNoTracking(), g => g.GenreId, gn => gn.Id, (g, gn) => new { g.WorkId, gn.Name })
                .ToListAsync(ct)).Select(x => (x.WorkId, x.Name)).ToList();
        Dictionary<(int, string), List<string>> genreMap = new();
        foreach ((long workId, string name) in genreRows)
        {
            if (!workKeyById.TryGetValue(workId, out (int TmdbId, string MediaType) key)) continue;
            if (!genreMap.TryGetValue(key, out List<string>? names)) genreMap[key] = names = new List<string>();
            if (names.Count < 3) names.Add(name);
        }

        List<LibraryItemResponse> items = new(pageItems.Count);
        foreach (var x in pageItems)
        {
            string? catName = x.CategoryId is long cid && catMap.TryGetValue(cid, out string? n) ? n : null;
            bool hasPoster = File.Exists(Path.Combine(_paths.PostersDir, $"{x.TmdbId}.jpg"));
            genreMap.TryGetValue((x.TmdbId, x.MediaType), out List<string>? gNames);
            items.Add(new LibraryItemResponse(
                x.TmdbId, x.MediaType, x.Title, x.Year, x.Rating, catName,
                x.FileCount, x.MissingFileCount, x.LatestArchivedAt, hasPoster,
                gNames ?? (IReadOnlyList<string>)Array.Empty<string>()));
        }

        return new LibraryListPage(items, total, page, pageSize);
    }

    public async Task<LibraryWorkDetailResponse> GetWorkDetailAsync(int tmdbId, string mediaType, CancellationToken ct = default)
    {
        if (tmdbId <= 0) throw new BusinessException("无效的 TmdbId");
        string mt = string.IsNullOrWhiteSpace(mediaType) ? "tv" : mediaType.Trim().ToLowerInvariant();

        // 惰性富化（失败/超时降级，不阻断详情）：远端限时 + 失败退避在富化服务内收口
        try { await _enrichment.EnrichAsync(tmdbId, mt, force: false, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { _logger.LogWarning("作品详情惰性富化超时（降级返回已有数据）：TmdbId={TmdbId}", tmdbId); }
        catch (Exception ex) { _logger.LogWarning(ex, "作品详情惰性富化失败（降级返回已有数据）：TmdbId={TmdbId}", tmdbId); }

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        List<FileProj> files = await LoadFilesAsync(db, tmdbId, mt, ct);
        if (files.Count == 0) throw new BusinessException("作品不存在或无记录");

        // 6 个并列集合 Include 必须 AsSplitQuery：否则单条 SQL 会做笛卡尔积
        // （演职员 × 类型 × 公司 × 电视台 × 关键词 × 季），大作品膨胀到十万级行、是详情慢的主因
        MediaWork? work = await db.MediaWorks.AsNoTracking()
            .Include(w => w.Credits)
            .Include(w => w.Genres)
            .Include(w => w.Companies)
            .Include(w => w.Networks)
            .Include(w => w.Keywords)
            .Include(w => w.Seasons)
            .AsSplitQuery()
            .FirstOrDefaultAsync(w => w.TmdbId == tmdbId && w.MediaType == mt, ct);

        // 文件行（实时存在性）+ 本地集映射
        List<LibraryFileItem> fileItems = files
            .OrderBy(f => f.Pi?.Season ?? int.MaxValue)
            .ThenBy(f => f.Pi?.Episode ?? int.MaxValue)
            .ThenBy(f => f.Pi?.EpisodeEnd ?? int.MaxValue)
            .ThenBy(f => f.Id)
            .Select(f => new LibraryFileItem(
                f.Id, f.FileName, f.FileSize, f.Pi?.Season, f.Pi?.Episode, f.Pi?.EpisodeEnd,
                f.Status, f.ParseSource, f.Confidence, f.TargetPath, f.ArchivedAt, f.UpdatedAt,
                ComputeExists(f)))
            .ToList();

        int completed = files.Count(r => r.Status == MediaItemStatus.Completed);
        int failed = files.Count(r => r.Status == MediaItemStatus.Failed);
        int awaiting = files.Count(r => r.Status == MediaItemStatus.AwaitingReview);
        int other = files.Count - completed - failed - awaiting;
        long totalSize = files.Sum(r => r.FileSize);
        DateTimeOffset? latestArchived = files.Max(r => r.ArchivedAt);

        // 关联展开（命中富化时）
        (List<WorkGenreDto> genres, List<WorkCompanyDto> companies, List<WorkCompanyDto> networks,
            List<WorkKeywordDto> keywords, List<WorkCreditDto> cast, List<WorkCreditDto> crew,
            List<WorkSeasonDto> seasons, long? catId) =
            work is null
                ? (new List<WorkGenreDto>(), new List<WorkCompanyDto>(), new List<WorkCompanyDto>(),
                   new List<WorkKeywordDto>(), new List<WorkCreditDto>(), new List<WorkCreditDto>(),
                   new List<WorkSeasonDto>(), (long?)null)
                : await BuildAssociationsAsync(db, work, files, ct);

        // 分类名：富化作品 CategoryId 优先，回退最近归档代表行
        long? categoryId = catId ?? files.OrderByDescending(f => f.ArchivedAt).FirstOrDefault(f => f.CategoryId != null)?.CategoryId;
        string? categoryName = categoryId is long cid
            ? await db.CategoryDefinitions.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;

        // 标量：Media_Work 优先，回退元数据缓存 / 解析
        TmdbMetadataCache? cache = work is null
            ? await db.TmdbMetadataCaches.AsNoTracking().FirstOrDefaultAsync(c => c.TmdbId == tmdbId && c.MediaType == mt, ct)
            : null;
        ParsedInfo? reprPi = ParsedInfo.FromJson(files.OrderByDescending(f => f.ArchivedAt).First().RawParsedInfo);

        string? title = work?.Title ?? cache?.Title ?? reprPi?.Title;
        string? originalTitle = work?.OriginalTitle ?? cache?.OriginalTitle;
        int? year = work?.Year ?? cache?.Year ?? reprPi?.Year;
        string? overview = work?.Overview ?? cache?.Overview;
        IReadOnlyList<string>? originCountry = work?.OriginCountry ?? cache?.OriginCountry;
        string? originalLanguage = work?.OriginalLanguage ?? cache?.OriginalLanguage;
        int? totalSeasons = work?.TotalSeasons ?? cache?.TotalSeasons;

        bool hasPoster = File.Exists(Path.Combine(_paths.PostersDir, $"{tmdbId}.jpg"));

        return new LibraryWorkDetailResponse(
            tmdbId, mt, title, originalTitle, year, overview, work?.Tagline,
            originCountry, originalLanguage, work?.TmdbStatus, work?.Homepage,
            work?.Runtime, work?.VoteAverage, work?.VoteCount, work?.ReleaseDate,
            totalSeasons, work?.TotalEpisodes, categoryName,
            hasPoster, work?.BackdropPath != null, work?.BackdropPath,
            genres, companies, networks, keywords, cast, crew, seasons,
            files.Count, completed, failed, awaiting, other, totalSize, latestArchived, fileItems);
    }

    public async Task<LibrarySeasonResponse> GetSeasonAsync(int tmdbId, int seasonNumber, string mediaType, CancellationToken ct = default)
    {
        if (tmdbId <= 0) throw new BusinessException("无效的 TmdbId");
        string mt = string.IsNullOrWhiteSpace(mediaType) ? "tv" : mediaType.Trim().ToLowerInvariant();

        try { await _enrichment.EnsureSeasonEpisodesAsync(tmdbId, mt, seasonNumber, force: false, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { _logger.LogWarning("分季惰性拉取超时（降级）：TmdbId={TmdbId} Season={Season}", tmdbId, seasonNumber); }
        catch (Exception ex) { _logger.LogWarning(ex, "分季惰性拉取失败（降级）：TmdbId={TmdbId} Season={Season}", tmdbId, seasonNumber); }

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        var workRow = await db.MediaWorks.AsNoTracking()
            .Where(w => w.TmdbId == tmdbId && w.MediaType == mt)
            .Select(w => new { w.Id })
            .FirstOrDefaultAsync(ct);

        string? seasonName = null;
        string? seasonOverview = null;
        List<MediaEpisode> episodes = new();
        if (workRow is not null)
        {
            var s = await db.MediaSeasons.AsNoTracking()
                .Where(x => x.WorkId == workRow.Id && x.SeasonNumber == seasonNumber)
                .Select(x => new { x.Name, x.Overview })
                .FirstOrDefaultAsync(ct);
            seasonName = s?.Name;
            seasonOverview = s?.Overview;
            episodes = await db.MediaEpisodes.AsNoTracking()
                .Where(e => e.WorkId == workRow.Id && e.SeasonNumber == seasonNumber)
                .OrderBy(e => e.EpisodeNumber)
                .ToListAsync(ct);
        }

        // 本地集映射（该季）
        List<FileProj> files = await LoadFilesAsync(db, tmdbId, mt, ct);
        Dictionary<int, LocalEp> localBySeasonEp = ExpandLocalEpisodes(files)
            .Where(kv => kv.Key.Season == seasonNumber)
            .ToDictionary(kv => kv.Key.Episode, kv => kv.Value);

        List<LibraryEpisodeDto> dtos = episodes.Select(e =>
        {
            bool has = localBySeasonEp.TryGetValue(e.EpisodeNumber, out LocalEp local);
            return new LibraryEpisodeDto(
                e.EpisodeNumber, e.Name, e.Overview, e.StillPath, e.AirDate, e.Runtime, e.VoteAverage,
                has, has ? local.Status : null, has ? ExistsByPaths(local.TargetPath, local.SourcePath) : null);
        }).ToList();

        return new LibrarySeasonResponse(seasonNumber, seasonName, seasonOverview, dtos);
    }

    public async Task<IReadOnlyList<LibraryRelatedItem>> GetRelatedAsync(int tmdbId, string mediaType, CancellationToken ct = default)
    {
        string mt = string.IsNullOrWhiteSpace(mediaType) ? "tv" : mediaType.Trim().ToLowerInvariant();
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        long self = await db.MediaWorks.AsNoTracking()
            .Where(w => w.TmdbId == tmdbId && w.MediaType == mt)
            .Select(w => w.Id)
            .FirstOrDefaultAsync(ct);
        if (self == 0) return Array.Empty<LibraryRelatedItem>();

        List<int> genreIds = await db.MediaWorkGenres.AsNoTracking().Where(x => x.WorkId == self).Select(x => x.GenreId).ToListAsync(ct);
        List<int> personIds = await db.MediaWorkCredits.AsNoTracking().Where(x => x.WorkId == self && x.CreditType == "cast")
            .OrderBy(x => x.Ord).Select(x => x.PersonId).Take(10).ToListAsync(ct);
        List<int> companyIds = await db.MediaWorkCompanies.AsNoTracking().Where(x => x.WorkId == self).Select(x => x.CompanyId).ToListAsync(ct);

        // 共享维度命中（每命中一个连接计 1 分）
        Dictionary<long, int> score = new();
        void Tally(IEnumerable<long> workIds)
        {
            foreach (long w in workIds) score[w] = score.GetValueOrDefault(w) + 1;
        }
        if (genreIds.Count > 0)
            Tally(await db.MediaWorkGenres.AsNoTracking().Where(x => x.WorkId != self && genreIds.Contains(x.GenreId)).Select(x => x.WorkId).ToListAsync(ct));
        if (personIds.Count > 0)
            Tally(await db.MediaWorkCredits.AsNoTracking().Where(x => x.WorkId != self && personIds.Contains(x.PersonId)).Select(x => x.WorkId).ToListAsync(ct));
        if (companyIds.Count > 0)
            Tally(await db.MediaWorkCompanies.AsNoTracking().Where(x => x.WorkId != self && companyIds.Contains(x.CompanyId)).Select(x => x.WorkId).ToListAsync(ct));

        if (score.Count == 0) return Array.Empty<LibraryRelatedItem>();

        List<long> candidateIds = score.OrderByDescending(kv => kv.Value).Take(RelatedCap * 3).Select(kv => kv.Key).ToList();
        var candWorks = await db.MediaWorks.AsNoTracking()
            .Where(w => candidateIds.Contains(w.Id))
            .Select(w => new { w.Id, w.TmdbId, w.MediaType, w.Title, w.Year, w.VoteAverage })
            .ToListAsync(ct);

        // 库内（有已完成文件）键集合：一次查出，替代逐候选 AnyAsync 的 N+1（候选可达 RelatedCap*3）
        List<int> candTmdbIds = candWorks.Select(w => w.TmdbId).Distinct().ToList();
        HashSet<(int, string)> inLibrary = (await db.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.Completed && m.TmdbId != null
                && m.TmdbMediaType != null && candTmdbIds.Contains(m.TmdbId.Value))
            .Select(m => new { TmdbId = m.TmdbId!.Value, MediaType = m.TmdbMediaType! })
            .Distinct().ToListAsync(ct))
            .Select(x => (x.TmdbId, x.MediaType))
            .ToHashSet();

        List<LibraryRelatedItem> result = new();
        foreach (var w in candWorks.OrderByDescending(w => score.GetValueOrDefault(w.Id)).ThenByDescending(w => w.VoteAverage ?? 0))
        {
            if (!inLibrary.Contains((w.TmdbId, w.MediaType))) continue;
            bool hasPoster = File.Exists(Path.Combine(_paths.PostersDir, $"{w.TmdbId}.jpg"));
            result.Add(new LibraryRelatedItem(w.TmdbId, w.MediaType, w.Title, w.Year, w.VoteAverage, hasPoster, score.GetValueOrDefault(w.Id)));
            if (result.Count >= RelatedCap) break;
        }
        return result;
    }

    public async Task<LibraryFacets> GetFacetsAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 库内作品键（有已完成文件）
        HashSet<(int, string)> libKeys = (await db.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.Completed && m.TmdbId != null && m.TmdbMediaType != null)
            .Select(m => new { TmdbId = m.TmdbId!.Value, MediaType = m.TmdbMediaType! })
            .Distinct().ToListAsync(ct))
            .Select(x => (x.TmdbId, x.MediaType)).ToHashSet();
        if (libKeys.Count == 0)
            return new LibraryFacets([], [], [], [], [], [], []);

        var libWorks = await db.MediaWorks.AsNoTracking()
            .Select(w => new { w.Id, w.TmdbId, w.MediaType, w.OriginalLanguage, w.OriginCountry })
            .ToListAsync(ct);
        var inLib = libWorks.Where(w => libKeys.Contains((w.TmdbId, w.MediaType))).ToList();
        HashSet<long> workIds = inLib.Select(w => w.Id).ToHashSet();
        if (workIds.Count == 0)
            return new LibraryFacets([], [], [], [], [], [], []);

        List<long> workIdList = workIds.ToList();

        List<FacetRef> genres = await FacetJoinAsync(
            db.MediaWorkGenres.Where(x => workIdList.Contains(x.WorkId)).Select(x => x.GenreId),
            db.MediaGenres, ct);
        List<FacetRef> companies = await FacetJoinAsync(
            db.MediaWorkCompanies.Where(x => workIdList.Contains(x.WorkId)).Select(x => x.CompanyId),
            db.MediaCompanies, ct);
        List<FacetRef> networks = await FacetJoinAsync(
            db.MediaWorkNetworks.Where(x => workIdList.Contains(x.WorkId)).Select(x => x.NetworkId),
            db.MediaNetworks, ct);
        List<FacetRef> keywords = await FacetJoinAsync(
            db.MediaWorkKeywords.Where(x => workIdList.Contains(x.WorkId)).Select(x => x.KeywordId),
            db.MediaKeywords, ct);

        // 演职员（仅 cast，按出现作品数 Top N）
        var personCounts = await db.MediaWorkCredits.AsNoTracking()
            .Where(x => workIdList.Contains(x.WorkId) && x.CreditType == "cast")
            .GroupBy(x => x.PersonId)
            .Select(g => new { PersonId = g.Key, Count = g.Select(c => c.WorkId).Distinct().Count() })
            .OrderByDescending(g => g.Count).Take(FacetCap)
            .ToListAsync(ct);
        List<int> personIds = personCounts.Select(p => p.PersonId).ToList();
        Dictionary<int, string> personNames = (await db.MediaPersons.AsNoTracking()
            .Where(p => personIds.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => p.Name);
        List<FacetRef> persons = personCounts
            .Where(p => personNames.ContainsKey(p.PersonId))
            .Select(p => new FacetRef(p.PersonId.ToString(), personNames[p.PersonId], p.Count))
            .ToList();

        // 国家 / 语言（标量，内存聚合）
        List<FacetRef> countries = inLib
            .Where(w => w.OriginCountry != null)
            .SelectMany(w => w.OriginCountry!)
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Select(g => new FacetRef(g.Key, g.Key, g.Count()))
            .ToList();
        List<FacetRef> languages = inLib
            .Where(w => !string.IsNullOrWhiteSpace(w.OriginalLanguage))
            .GroupBy(w => w.OriginalLanguage!)
            .OrderByDescending(g => g.Count())
            .Select(g => new FacetRef(g.Key, g.Key, g.Count()))
            .ToList();

        return new LibraryFacets(genres, persons, companies, networks, keywords, countries, languages);
    }

    public async Task<ScanExistenceResult> ScanExistenceAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<MediaItem> items = await db.MediaItems
            .Where(m => m.Status == MediaItemStatus.Completed && m.TargetPath != null)
            .ToListAsync(ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int missing = 0;
        foreach (MediaItem m in items)
        {
            bool isMissing = !File.Exists(m.TargetPath);
            if (isMissing) missing++;
            m.MarkFileChecked(isMissing, now);
        }
        await db.SaveChangesAsync(ct);
        return new ScanExistenceResult(items.Count, missing);
    }

    public async Task<AudioRescanResult> RescanAudioAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 读音频设置一次（循环外）：总开关 + ffmpeg 路径 + 不兼容 codec 清单
        Dictionary<string, string?> s = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == AudioProbeSupport.CheckEnabledKey || x.Key == AudioProbeSupport.FfmpegPathKey
                     || x.Key == AudioProbeSupport.IncompatibleCodecsKey)
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);

        if (!AudioProbeSupport.IsSettingTrue(s, AudioProbeSupport.CheckEnabledKey))
            throw new BusinessException("音频不兼容轨检查未启用，请先在「设置 → 常规 → 音频」开启总开关并配置 ffmpeg 路径");
        IReadOnlySet<string> codecs = AudioProbeSupport.ParseCodecList(AudioProbeSupport.GetSetting(s, AudioProbeSupport.IncompatibleCodecsKey));
        if (codecs.Count == 0)
            throw new BusinessException("不兼容编解码器清单为空，请在「设置 → 常规 → 音频」填写（如 av3a）");
        (string? ffprobe, _) = AudioProbeSupport.ResolveFfmpegTools(AudioProbeSupport.GetSetting(s, AudioProbeSupport.FfmpegPathKey));
        if (ffprobe is null)
            throw new BusinessException("ffprobe 不存在，请检查「设置 → 常规 → 音频」的 ffmpeg 路径");

        // 仅扫「未探测过(AudioCodecs==null)的已完成记录」：新入库已在归档时探测，存量首扫即全量、重复触发不重扫已标记的
        List<MediaItem> items = await db.MediaItems
            .Where(m => m.Status == MediaItemStatus.Completed && m.TargetPath != null && m.AudioCodecs == null)
            .ToListAsync(ct);

        int probed = 0, incompatible = 0, skipped = 0;
        foreach (MediaItem m in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // 探 TargetPath（已归档落点）；缺文件 / ffprobe 失败 → Unavailable，跳过不打标（下次可重试）
                AudioProbeResult probe = await _audioProbe.ProbeAsync(ffprobe, m.TargetPath!, codecs, ct);
                if (!probe.Available)
                {
                    skipped++;
                    continue;
                }
                string? codecsCsv = probe.Streams.Count > 0
                    ? string.Join(",", probe.Streams.Select(x => x.Codec).Where(c => c.Length > 0))
                    : null;
                m.RefreshAudioProbe(codecsCsv, probe.HasIncompatible);
                await db.SaveChangesAsync(ct); // 逐条保存：整库探测耗时长，取消 / 崩溃不丢已探测的
                probed++;
                if (probe.HasIncompatible) incompatible++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "存量音频重扫单条失败（跳过）：{Target}", m.TargetPath);
                skipped++;
            }
        }
        return new AudioRescanResult(items.Count, probed, incompatible, skipped);
    }

    // ---------- 孤儿文件找回（删了记录但文件留在归档库里的媒体文件）----------

    /// <summary>从文件名 / 任一级父目录名提取 {tmdb-NNN} 标记（大小写不敏感，兼容历史大写写法）</summary>
    private static readonly Regex TmdbMarkerRegex = new(@"\{tmdb-(\d+)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<OrphanScanResult> ScanOrphansAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        List<CategoryDefinition> cats = await db.CategoryDefinitions.AsNoTracking().ToListAsync(ct);

        // 「已认领」= 库内任何仍持有 TargetPath 的记录（不限 Completed：在途 Archiving 等持 TargetPath 的也算占用，
        // 避免把正在归档的文件误判为孤儿）。规范化后入 HashSet 供 O(1) 比对。
        HashSet<string> claimed = (await db.MediaItems.AsNoTracking()
                .Where(m => m.TargetPath != null)
                .Select(m => m.TargetPath!)
                .ToListAsync(ct))
            .Select(NormalizePath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlySet<string> exts = _extensions.GetEnabledExtensions();
        List<OrphanFileDto> orphans = new();
        foreach (CategoryDefinition cat in cats)
        {
            if (string.IsNullOrWhiteSpace(cat.TargetRoot) || !Directory.Exists(cat.TargetRoot)) continue;
            foreach (string file in EnumerateMediaFiles(cat.TargetRoot, exts, ct))
            {
                ct.ThrowIfCancellationRequested();
                string? norm = NormalizePath(file);
                if (norm is null || claimed.Contains(norm)) continue;   // 已有记录占用 → 非孤儿
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { /* 读不到大小不阻断 */ }
                orphans.Add(new OrphanFileDto(file, Path.GetFileName(file), size, cat.Id, cat.Name, TryParseTmdbMarker(file)));
            }
        }
        _logger.LogInformation("孤儿扫描完成：{Count} 个孤儿文件（{Cats} 个分类根）", orphans.Count, cats.Count);
        return new OrphanScanResult(orphans.Count, orphans);
    }

    public async Task<ClaimOrphansResult> ClaimOrphansAsync(ClaimOrphansRequest req, CancellationToken ct = default)
    {
        if (req.Paths is not { Count: > 0 })
            throw new BusinessException("未指定要认领的孤儿文件");

        int admitted = 0, skipped = 0;
        foreach (string path in req.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) { skipped++; continue; }
            // 重新投入管线（watchFolderId=0 手动来源）：孤儿带 {tmdb-NNN} 标记 → 强制匹配锚定，零搜索零 AI；
            // 归档层识别「源已在规范目标位」→ 直接登记 Completed 不搬动。已有同源记录在处理则 AdmitAsync 返 false。
            bool ok = await _intake.AdmitAsync(path, 0, PendingFileSource.Manual, ct);
            if (ok) admitted++; else skipped++;
        }
        _logger.LogInformation("孤儿认领入队：请求 {Total}，入队 {Admitted}，跳过 {Skipped}", req.Paths.Count, admitted, skipped);
        return new ClaimOrphansResult(req.Paths.Count, admitted, skipped);
    }

    /// <summary>枚举目录下媒体文件（递归 + 跳过不可访问，与 ScanService 同款 EnumerationOptions，行为对齐避免漂移）</summary>
    private IReadOnlyList<string> EnumerateMediaFiles(string root, IReadOnlySet<string> exts, CancellationToken ct)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            MatchType = MatchType.Win32,
        };
        IEnumerator<string> e;
        try { e = Directory.EnumerateFiles(root, "*", options).GetEnumerator(); }
        catch (Exception ex) { _logger.LogWarning(ex, "孤儿扫描枚举目录失败：{Path}", root); return Array.Empty<string>(); }

        List<string> files = new();
        using (e)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { if (!e.MoveNext()) break; }
                catch (Exception ex) { _logger.LogWarning(ex, "孤儿扫描枚举中途失败（已收集 {Count}）：{Path}", files.Count, root); break; }
                string f = e.Current;
                if (exts.Contains(Path.GetExtension(f))) files.Add(f);
            }
        }
        return files;
    }

    /// <summary>路径规范化（绝对 + 统一分隔符）供 OrdinalIgnoreCase 比对；非法路径返 null</summary>
    private static string? NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>从文件名 / 各级父目录名解析 {tmdb-NNN} 标记的 TMDB id（剧集单集在父目录带标记），无则 null</summary>
    private static int? TryParseTmdbMarker(string path)
    {
        Match m = TmdbMarkerRegex.Match(path);
        return m.Success && int.TryParse(m.Groups[1].Value, out int id) ? id : null;
    }

    public string? GetPosterPath(int tmdbId)
    {
        if (tmdbId <= 0) return null;
        string path = Path.Combine(_paths.PostersDir, $"{tmdbId}.jpg");
        return File.Exists(path) ? path : null;
    }

    // ---------- 私有助手 ----------

    private sealed record FileProj(
        long Id, string FileName, long FileSize, string? RawParsedInfo, MediaItemStatus Status,
        ParseSource? ParseSource, double? Confidence, string? TargetPath, string? SourcePath,
        DateTimeOffset? ArchivedAt, DateTimeOffset UpdatedAt, long? CategoryId)
    {
        public ParsedInfo? Pi => ParsedInfo.FromJson(RawParsedInfo);
    }

    private readonly record struct LocalEp(MediaItemStatus Status, string? TargetPath, string? SourcePath);

    private static async Task<List<FileProj>> LoadFilesAsync(PmmDbContext db, int tmdbId, string mt, CancellationToken ct)
        => await db.MediaItems.AsNoTracking()
            .Where(m => m.TmdbId == tmdbId && m.TmdbMediaType == mt)
            .Select(m => new FileProj(
                m.Id, m.FileName, m.FileSize, m.ParsedInfo, m.Status, m.ParseSource, m.Confidence,
                m.TargetPath, m.SourcePath, m.ArchivedAt, m.UpdatedAt, m.CategoryId))
            .ToListAsync(ct);

    private static bool? ComputeExists(FileProj f)
    {
        string? path = f.Status == MediaItemStatus.Completed ? f.TargetPath : (f.TargetPath ?? f.SourcePath);
        return ExistsByPaths(path, f.SourcePath);
    }

    private static bool? ExistsByPaths(string? primary, string? fallback)
    {
        string? path = !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
        if (string.IsNullOrWhiteSpace(path)) return null;
        return File.Exists(path);
    }

    /// <summary>把文件按季/集（含双集范围）展开为 (季,集) → 本地文件信息</summary>
    private static Dictionary<(int Season, int Episode), LocalEp> ExpandLocalEpisodes(IEnumerable<FileProj> files)
    {
        Dictionary<(int, int), LocalEp> map = new();
        foreach (FileProj f in files)
        {
            ParsedInfo? pi = f.Pi;
            if (pi?.Season is not int season || pi.Episode is not int ep) continue;
            int end = pi.EpisodeEnd is int ee && ee >= ep ? ee : ep;
            for (int e = ep; e <= end; e++)
            {
                (int, int) key = (season, e);
                // 优先保留已完成 / 有归档路径的文件
                if (map.TryGetValue(key, out LocalEp existing) && existing.Status == MediaItemStatus.Completed) continue;
                map[key] = new LocalEp(f.Status, f.TargetPath, f.SourcePath);
            }
        }
        return map;
    }

    private static async Task<(List<WorkGenreDto>, List<WorkCompanyDto>, List<WorkCompanyDto>, List<WorkKeywordDto>,
        List<WorkCreditDto>, List<WorkCreditDto>, List<WorkSeasonDto>, long?)> BuildAssociationsAsync(
        PmmDbContext db, MediaWork work, List<FileProj> files, CancellationToken ct)
    {
        // 维度名解析
        List<int> genreIds = work.Genres.Select(g => g.GenreId).ToList();
        List<int> companyIds = work.Companies.Select(c => c.CompanyId).ToList();
        List<int> networkIds = work.Networks.Select(n => n.NetworkId).ToList();
        List<int> keywordIds = work.Keywords.Select(k => k.KeywordId).ToList();
        List<int> personIds = work.Credits.Select(c => c.PersonId).Distinct().ToList();

        Dictionary<int, MediaGenre> genreDim = (await db.MediaGenres.AsNoTracking().Where(g => genreIds.Contains(g.Id)).ToListAsync(ct)).ToDictionary(g => g.Id);
        Dictionary<int, MediaCompany> companyDim = (await db.MediaCompanies.AsNoTracking().Where(c => companyIds.Contains(c.Id)).ToListAsync(ct)).ToDictionary(c => c.Id);
        Dictionary<int, MediaNetwork> networkDim = (await db.MediaNetworks.AsNoTracking().Where(n => networkIds.Contains(n.Id)).ToListAsync(ct)).ToDictionary(n => n.Id);
        Dictionary<int, MediaKeyword> keywordDim = (await db.MediaKeywords.AsNoTracking().Where(k => keywordIds.Contains(k.Id)).ToListAsync(ct)).ToDictionary(k => k.Id);
        Dictionary<int, MediaPerson> personDim = (await db.MediaPersons.AsNoTracking().Where(p => personIds.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id);

        List<WorkGenreDto> genres = work.Genres
            .Where(g => genreDim.ContainsKey(g.GenreId))
            .Select(g => new WorkGenreDto(g.GenreId, genreDim[g.GenreId].Name)).ToList();
        List<WorkCompanyDto> companies = work.Companies
            .Where(c => companyDim.ContainsKey(c.CompanyId))
            .Select(c => new WorkCompanyDto(c.CompanyId, companyDim[c.CompanyId].Name, companyDim[c.CompanyId].LogoPath, companyDim[c.CompanyId].OriginCountry)).ToList();
        List<WorkCompanyDto> networks = work.Networks
            .Where(n => networkDim.ContainsKey(n.NetworkId))
            .Select(n => new WorkCompanyDto(n.NetworkId, networkDim[n.NetworkId].Name, networkDim[n.NetworkId].LogoPath, networkDim[n.NetworkId].OriginCountry)).ToList();
        List<WorkKeywordDto> keywords = work.Keywords
            .Where(k => keywordDim.ContainsKey(k.KeywordId))
            .Select(k => new WorkKeywordDto(k.KeywordId, keywordDim[k.KeywordId].Name)).ToList();

        List<WorkCreditDto> cast = work.Credits
            .Where(c => c.CreditType == "cast" && personDim.ContainsKey(c.PersonId))
            .OrderBy(c => c.Ord ?? int.MaxValue)
            .Select(c => new WorkCreditDto(c.PersonId, personDim[c.PersonId].Name, personDim[c.PersonId].ProfilePath, c.Character, null))
            .ToList();
        List<WorkCreditDto> crew = work.Credits
            .Where(c => c.CreditType == "crew" && personDim.ContainsKey(c.PersonId))
            .Select(c => new WorkCreditDto(c.PersonId, personDim[c.PersonId].Name, personDim[c.PersonId].ProfilePath, null, c.Job))
            .ToList();

        // 季摘要 + 本地已入库集数
        Dictionary<(int, int), LocalEp> local = ExpandLocalEpisodes(files);
        Dictionary<int, int> localPerSeason = local.Keys.GroupBy(k => k.Item1).ToDictionary(g => g.Key, g => g.Count());
        List<WorkSeasonDto> seasons = work.Seasons
            .OrderBy(s => s.SeasonNumber)
            .Select(s => new WorkSeasonDto(
                s.SeasonNumber, s.Name, s.Overview, s.PosterPath, s.AirDate, s.EpisodeCount,
                localPerSeason.GetValueOrDefault(s.SeasonNumber)))
            .ToList();

        return (genres, companies, networks, keywords, cast, crew, seasons, work.CategoryId);
    }

    private static async Task<List<FacetRef>> FacetJoinAsync<TDim>(
        IQueryable<int> idSource, DbSet<TDim> dimSet, CancellationToken ct)
        where TDim : class, IMediaDimension
    {
        var counts = await idSource
            .GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(FacetCap)
            .ToListAsync(ct);
        List<int> ids = counts.Select(c => c.Id).ToList();
        Dictionary<int, string> nameMap = (await dimSet.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(ct)).ToDictionary(n => n.Id, n => n.Name);
        return counts
            .Where(c => nameMap.ContainsKey(c.Id))
            .Select(c => new FacetRef(c.Id.ToString(), nameMap[c.Id], c.Count))
            .ToList();
    }
}
