using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Library;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Library;

/// <summary>媒体作品富化编排 — TMDB 富化详情 → Media_Work + 维度/关联/季集</summary>
/// <remarks>
/// 维度实体（Media_Person/Genre/Company/Network/Keyword）按 TMDB id upsert 共享；连接表经 MediaWork.Replace* 原子替换。
/// 新作品先 SaveChanges 取自增 Id 再建子行（子行 FK 需真实 WorkId）。TTL 用 Tmdb_Setting.MetadataCacheHours，
/// EnrichedAt 未过期且非 force 直接跳过，避免重复打 TMDB。海报沿用 IPosterDownloader 本地缓存（背景图/人物照走图片代理按需取）。
/// </remarks>
internal sealed class WorkEnrichmentService : IWorkEnrichmentService
{
    private const long TmdbSettingId = 1;

    /// <summary>惰性富化单次远端调用的限时 — 远端不可达时最多等这么久，避免吃满 TmdbClient 的 30s 超时</summary>
    private static readonly TimeSpan RemoteCallTimeout = TimeSpan.FromSeconds(6);

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly ITmdbClient _client;
    private readonly IPosterDownloader _poster;
    private readonly AppPaths _paths;
    private readonly WorkEnrichmentBackoff _backoff;
    private readonly ITmdbSearchService _catalogue;
    private readonly bool _usesSharedCatalogue;
    private readonly ILogger<WorkEnrichmentService> _logger;

    public WorkEnrichmentService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        ITmdbClient client,
        IPosterDownloader poster,
        AppPaths paths,
        WorkEnrichmentBackoff backoff,
        ILogger<WorkEnrichmentService> logger,
        ITmdbSearchService? catalogue = null)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _client = client;
        _poster = poster;
        _paths = paths;
        _backoff = backoff;
        _logger = logger;
        _usesSharedCatalogue = catalogue is not null;
        _catalogue = catalogue ?? new TmdbSearchService(dbFactory, protector, client, poster, paths,
            NullLogger<TmdbSearchService>.Instance);
    }

    public async Task<bool> EnrichAsync(int tmdbId, string mediaType, bool force, CancellationToken ct = default)
    {
        string mt = mediaType.ToLowerInvariant();
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 1) 轻量查 TTL，命中且新鲜则跳过（不加载关联图）
        var meta = await db.MediaWorks.AsNoTracking()
            .Where(w => w.TmdbId == tmdbId && w.MediaType == mt)
            .Select(w => new { w.Id, w.EnrichedAt })
            .FirstOrDefaultAsync(ct);

        TmdbSetting setting = await LoadSettingAsync(db, ct);
        DateTimeOffset metaExpiry = DateTimeOffset.UtcNow.AddHours(-setting.MetadataCacheHours);
        if (meta is not null && !force && meta.EnrichedAt is DateTimeOffset ea && ea >= metaExpiry)
        {
            _logger.LogInformation("作品富化命中本地缓存(TTL 未过期)，跳过远端：tmdbId={TmdbId}, type={Type}", tmdbId, mt);
            return false;
        }

        // 2) 远端富化前先看失败退避窗口：远端近期不可达则跳过、即时降级，避免反复打满超时
        string backoffKey = $"work:{mt}:{tmdbId}";
        if (!force && _backoff.IsBackedOff(backoffKey))
        {
            _logger.LogInformation("作品富化处于失败退避窗口，跳过远端（降级返回已有数据）：tmdbId={TmdbId}, type={Type}", tmdbId, mt);
            return false;
        }

        string apiKey = DecryptApiKey(setting);
        _logger.LogInformation("作品富化远端拉取(append credits/keywords)：tmdbId={TmdbId}, type={Type}, force={Force}", tmdbId, mt, force);
        TmdbEnrichedDetails d = await CallRemoteAsync(
            backoffKey,
            token => _client.GetEnrichedDetailsAsync(tmdbId, mt, apiKey, setting.Language, token),
            ct);

        ct.ThrowIfCancellationRequested();
        ValidateEnrichedIdentity(d, tmdbId, mt);

        // 3) 取/建 tracked 作品（新作品先存一次拿 Id）
        MediaWork work;
        if (meta is null)
        {
            work = MediaWork.CreateMinimal(tmdbId, mt, d.Title, d.Year);
            db.MediaWorks.Add(work);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            IQueryable<MediaWork> q = db.MediaWorks
                .Include(w => w.Credits)
                .Include(w => w.Genres)
                .Include(w => w.Companies)
                .Include(w => w.Networks)
                .Include(w => w.Keywords)
                .Include(w => w.Seasons);
            // 多集合 Include 必须 AsSplitQuery，避免单条 SQL 笛卡尔积膨胀
            work = await q.AsSplitQuery().FirstAsync(w => w.Id == meta.Id, ct);
        }

        // 4) upsert 共享维度
        await UpsertPersonsAsync(db, d.Cast.Concat(d.Crew), ct);
        await UpsertGenresAsync(db, d.Genres, ct);
        await UpsertCompaniesAsync(db, d.Companies, ct);
        await UpsertNetworksAsync(db, d.Networks, ct);
        await UpsertKeywordsAsync(db, d.Keywords, ct);

        // 5) 标量 + 关联原子替换
        work.UpsertScalars(
            d.Title, d.OriginalTitle, d.Year, KeepText(d.Overview, work.Overview), d.Tagline,
            d.PosterPath, d.BackdropPath, d.Runtime, d.VoteAverage, d.VoteCount,
            d.ReleaseDate, d.TmdbStatus, d.OriginalLanguage, d.OriginCountry?.ToList(),
            d.Homepage, d.TotalSeasons, d.TotalEpisodes);

        work.ReplaceCredits(
            d.Cast.Select(c => new CreditSeed(c.PersonId, "cast", c.Character, c.Order, null, null))
                .Concat(d.Crew.Select(c => new CreditSeed(c.PersonId, "crew", null, null, c.Job, c.Department))));
        work.ReplaceGenres(d.Genres.Select(g => g.Id));
        work.ReplaceCompanies(d.Companies.Select(c => c.Id));
        work.ReplaceNetworks(d.Networks.Select(n => n.Id));
        work.ReplaceKeywords(d.Keywords.Select(k => k.Id));
        // 库内简介与已抓取分集是独立缓存；详情刷新不能在季请求成功前清空它们。
        work.ReplaceSeasons(MergeSeasonSummaries(work.Seasons, d.Seasons));
        work.MarkEnriched(DateTimeOffset.UtcNow);

        // 6) 同步分类（最近归档代表行 CategoryId）
        long? catId = await db.MediaItems.AsNoTracking()
            .Where(m => m.TmdbId == tmdbId && m.TmdbMediaType == mt && m.CategoryId != null)
            .OrderByDescending(m => m.ArchivedAt)
            .Select(m => m.CategoryId)
            .FirstOrDefaultAsync(ct);
        work.SetCategory(catId);

        await db.SaveChangesAsync(ct);
        await TryCachePosterAsync(tmdbId, d.PosterPath, ct);
        return true;
    }

    public async Task<int> EnrichAllAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        var works = await db.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.Completed && m.TmdbId != null && m.TmdbMediaType != null)
            .Select(m => new { TmdbId = m.TmdbId!.Value, MediaType = m.TmdbMediaType! })
            .Distinct()
            .ToListAsync(ct);

        int n = 0;
        foreach (var w in works)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EnrichAsync(w.TmdbId, w.MediaType, force: false, ct);
                n++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "整库富化单部失败（跳过继续）：TmdbId={TmdbId} MediaType={MediaType}", w.TmdbId, w.MediaType);
            }
        }
        return n;
    }

    public async Task EnsureSeasonEpisodesAsync(int tmdbId, string mediaType, int seasonNumber, bool force, CancellationToken ct = default)
    {
        if (!mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase)) return;

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaWork? work = await db.MediaWorks
            .Include(w => w.Episodes.Where(e => e.SeasonNumber == seasonNumber))
            .FirstOrDefaultAsync(w => w.TmdbId == tmdbId && w.MediaType == "tv", ct);

        // 作品尚未富化：先富化再取（EnrichAsync 用独立上下文，之后重新查）
        if (work is null)
        {
            await EnrichAsync(tmdbId, "tv", force: false, ct);
            work = await db.MediaWorks
                .Include(w => w.Episodes.Where(e => e.SeasonNumber == seasonNumber))
                .FirstOrDefaultAsync(w => w.TmdbId == tmdbId && w.MediaType == "tv", ct);
            if (work is null) return;
        }

        string backoffKey = $"season:{tmdbId}:{seasonNumber}";
        // 注入统一目录时先读它的新鲜缓存；人工刚刷新成功不能被此前库内失败退避挡住。
        if (!_usesSharedCatalogue && !force && _backoff.IsBackedOff(backoffKey))
        {
            _logger.LogInformation("分季处于失败退避窗口，跳过远端（降级返回已有数据）：tmdbId={TmdbId}, season={Season}", tmdbId, seasonNumber);
            return;
        }

        // 目录服务统一判断 TTL，不能仅因库内已有分集就永久跳过远端。
        TmdbSeasonCatalogueResult result = await CallRemoteAsync(backoffKey, async token =>
        {
            TmdbSeasonCatalogueResult refreshed = await _catalogue.GetSeasonCatalogueAsync(tmdbId, seasonNumber, force, token);
            if (refreshed.RefreshError is not null)
                throw new TmdbClientException(refreshed.RefreshError);
            if (refreshed.TmdbId != tmdbId || refreshed.MediaType != "tv"
                || refreshed.Season?.SeasonNumber != seasonNumber)
                throw new TmdbClientException("TMDB 季目录身份与库内作品不一致");
            return refreshed;
        }, ct);
        ct.ThrowIfCancellationRequested();
        TmdbSeasonDetail season = result.Season!;
        work.ReplaceSeasonEpisodes(seasonNumber, MergeSeasonEpisodes(work.Episodes, seasonNumber, season.Episodes));
        await db.SaveChangesAsync(ct);
    }

    private static void ValidateEnrichedIdentity(TmdbEnrichedDetails details, int tmdbId, string mediaType)
    {
        if (details.TmdbId != tmdbId || !string.Equals(details.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            throw new TmdbClientException("TMDB 富化结果与请求作品身份不一致");
        if (string.IsNullOrWhiteSpace(details.Title) && string.IsNullOrWhiteSpace(details.OriginalTitle))
            throw new TmdbClientException("TMDB 富化详情缺少作品名称，保留已有资料");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(details.RawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || (doc.RootElement.TryGetProperty("id", out JsonElement id)
                    && (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int responseId) || responseId != tmdbId)))
                throw new TmdbClientException("TMDB 富化原始详情身份或格式不正确");
        }
        catch (JsonException ex)
        {
            throw new TmdbClientException("TMDB 富化原始详情格式不正确", inner: ex);
        }
    }

    /// <summary>仅库内显示保留缺失的旧简介，不修改正典目录</summary>
    private static string? KeepText(string? fresh, string? old)
        => string.IsNullOrWhiteSpace(fresh) ? old : fresh;

    private static IReadOnlyList<SeasonSeed> MergeSeasonSummaries(
        IReadOnlyCollection<MediaSeason> existing, IReadOnlyList<TmdbSeasonSummary> fresh)
    {
        Dictionary<int, SeasonSeed> merged = existing.ToDictionary(s => s.SeasonNumber,
            s => new SeasonSeed(s.SeasonNumber, s.Name, s.Overview, s.PosterPath, s.AirDate, s.EpisodeCount));
        foreach (TmdbSeasonSummary s in fresh)
        {
            merged.TryGetValue(s.SeasonNumber, out SeasonSeed old);
            merged[s.SeasonNumber] = new SeasonSeed(s.SeasonNumber, KeepText(s.Name, old.Name),
                KeepText(s.Overview, old.Overview), KeepText(s.PosterPath, old.PosterPath),
                s.AirDate ?? old.AirDate, s.EpisodeCount);
        }
        return merged.Values.OrderBy(s => s.SeasonNumber).ToList();
    }

    private static IReadOnlyList<EpisodeSeed> MergeSeasonEpisodes(
        IReadOnlyCollection<MediaEpisode> existing, int seasonNumber, IReadOnlyList<TmdbEpisodeRef> fresh)
    {
        Dictionary<int, EpisodeSeed> merged = existing.Where(e => e.SeasonNumber == seasonNumber)
            .ToDictionary(e => e.EpisodeNumber, e => new EpisodeSeed(seasonNumber, e.EpisodeNumber,
                e.Name, e.Overview, e.StillPath, e.AirDate, e.Runtime, e.VoteAverage));
        foreach (TmdbEpisodeRef e in fresh)
        {
            merged.TryGetValue(e.EpisodeNumber, out EpisodeSeed old);
            merged[e.EpisodeNumber] = new EpisodeSeed(seasonNumber, e.EpisodeNumber,
                KeepText(e.Name, old.Name), KeepText(e.Overview, old.Overview), KeepText(e.StillPath, old.StillPath),
                e.AirDate ?? old.AirDate, e.Runtime ?? old.Runtime, e.VoteAverage ?? old.VoteAverage);
        }
        return merged.Values.OrderBy(e => e.EpisodeNumber).ToList();
    }

    // ---------- 远端调用限时 + 失败退避 ----------

    /// <summary>远端富化调用统一收口：单次限时 + 失败/超时登记退避、成功解除</summary>
    /// <remarks>
    /// 用链接令牌叠加 RemoteCallTimeout，远端卡住时最多等这么久即降级（不吃满 TmdbClient 30s）；
    /// 非「请求被取消」的任何失败（限时到点 / 网络异常 / TMDB 非 2xx）都登记退避键，窗口内同键直接跳过远端；一次成功即解除。
    /// </remarks>
    private async Task<T> CallRemoteAsync<T>(string backoffKey, Func<CancellationToken, Task<T>> remote, CancellationToken ct)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RemoteCallTimeout);
        try
        {
            T result = await remote(cts.Token);
            _backoff.Clear(backoffKey);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 真·请求中断（客户端断开），不计退避
        }
        catch (Exception)
        {
            _backoff.MarkFailure(backoffKey); // 限时到点 / 网络异常 / TMDB 非 2xx → 登记退避
            throw;
        }
    }

    // ---------- 维度 upsert（仅插入缺失，TMDB id 为键） ----------

    private static async Task UpsertPersonsAsync(PmmDbContext db, IEnumerable<TmdbCreditRef> credits, CancellationToken ct)
    {
        List<TmdbCreditRef> distinct = credits
            .GroupBy(c => c.PersonId)
            .Select(g => g.First())
            .ToList();
        if (distinct.Count == 0) return;

        List<int> ids = distinct.Select(c => c.PersonId).ToList();
        HashSet<int> existing = (await db.MediaPersons.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        foreach (TmdbCreditRef c in distinct)
        {
            if (existing.Contains(c.PersonId)) continue;
            db.MediaPersons.Add(new MediaPerson
            {
                Id = c.PersonId,
                Name = c.Name,
                ProfilePath = c.ProfilePath,
                KnownForDepartment = c.KnownForDepartment,
            });
            existing.Add(c.PersonId);
        }
    }

    private static async Task UpsertGenresAsync(PmmDbContext db, IReadOnlyList<TmdbGenreRef> genres, CancellationToken ct)
    {
        if (genres.Count == 0) return;
        List<int> ids = genres.Select(g => g.Id).Distinct().ToList();
        HashSet<int> existing = (await db.MediaGenres.Where(g => ids.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct)).ToHashSet();
        foreach (TmdbGenreRef g in genres)
        {
            if (g.Id == 0 || existing.Contains(g.Id)) continue;
            db.MediaGenres.Add(new MediaGenre { Id = g.Id, Name = g.Name });
            existing.Add(g.Id);
        }
    }

    private static async Task UpsertCompaniesAsync(PmmDbContext db, IReadOnlyList<TmdbCompanyRef> companies, CancellationToken ct)
    {
        if (companies.Count == 0) return;
        List<int> ids = companies.Select(c => c.Id).Distinct().ToList();
        HashSet<int> existing = (await db.MediaCompanies.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        foreach (TmdbCompanyRef c in companies)
        {
            if (c.Id == 0 || existing.Contains(c.Id)) continue;
            db.MediaCompanies.Add(new MediaCompany { Id = c.Id, Name = c.Name, LogoPath = c.LogoPath, OriginCountry = c.OriginCountry });
            existing.Add(c.Id);
        }
    }

    private static async Task UpsertNetworksAsync(PmmDbContext db, IReadOnlyList<TmdbNetworkRef> networks, CancellationToken ct)
    {
        if (networks.Count == 0) return;
        List<int> ids = networks.Select(n => n.Id).Distinct().ToList();
        HashSet<int> existing = (await db.MediaNetworks.Where(n => ids.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct)).ToHashSet();
        foreach (TmdbNetworkRef n in networks)
        {
            if (n.Id == 0 || existing.Contains(n.Id)) continue;
            db.MediaNetworks.Add(new MediaNetwork { Id = n.Id, Name = n.Name, LogoPath = n.LogoPath, OriginCountry = n.OriginCountry });
            existing.Add(n.Id);
        }
    }

    private static async Task UpsertKeywordsAsync(PmmDbContext db, IReadOnlyList<TmdbKeywordRef> keywords, CancellationToken ct)
    {
        if (keywords.Count == 0) return;
        List<int> ids = keywords.Select(k => k.Id).Distinct().ToList();
        HashSet<int> existing = (await db.MediaKeywords.Where(k => ids.Contains(k.Id)).Select(k => k.Id).ToListAsync(ct)).ToHashSet();
        foreach (TmdbKeywordRef k in keywords)
        {
            if (k.Id == 0 || existing.Contains(k.Id)) continue;
            db.MediaKeywords.Add(new MediaKeyword { Id = k.Id, Name = k.Name });
            existing.Add(k.Id);
        }
    }

    // ---------- 复用 TmdbSearchService 同款小工具 ----------

    private async Task TryCachePosterAsync(int tmdbId, string? posterPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(posterPath)) return;
        try
        {
            await _poster.DownloadAsync(tmdbId, posterPath, _paths.CacheDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "海报下载失败（不影响富化）：TmdbId={TmdbId}", tmdbId);
        }
    }

    private static async Task<TmdbSetting> LoadSettingAsync(PmmDbContext db, CancellationToken ct)
    {
        TmdbSetting? s = await db.TmdbSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == TmdbSettingId, ct);
        return s ?? throw new BusinessException("TMDB 配置单例缺失（请检查 Migration 种子）");
    }

    private string DecryptApiKey(TmdbSetting setting)
    {
        if (string.IsNullOrEmpty(setting.ApiKeyEncrypted))
            throw new BusinessException("尚未配置 TMDB ApiKey");
        return _protector.Unprotect(setting.ApiKeyEncrypted);
    }
}
