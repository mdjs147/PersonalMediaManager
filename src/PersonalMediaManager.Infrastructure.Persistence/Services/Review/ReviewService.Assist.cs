using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Review;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Review;

internal sealed partial class ReviewService
{
    private static readonly Regex ExplicitSeasonEpisode = new(
        @"(?<![A-Za-z0-9])(?:(?:Season|S)[ ._-]*\d{1,2}[ ._-]*(?:Episode|E)[ ._-]*\d+|\d{1,2}x\d{1,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<ReviewEpisodeHintsResult> EpisodeHintsAsync(ReviewEpisodeHintsRequest req, CancellationToken ct = default)
    {
        if (req.Items is not { Count: > 0 and <= 100 }) throw new BusinessException("每次可提取 1 至 100 条记录");
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        long[] ids = req.Items.Select(x => x.Id).Distinct().ToArray();
        Dictionary<long, MediaItem> rows = await db.MediaItems.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        List<string> roots = await db.WatchFolders.AsNoTracking().Select(w => w.Path).ToListAsync(ct);
        List<ReviewEpisodeHint> results = [];
        foreach (ReviewEpisodeHintItem request in req.Items)
        {
            ct.ThrowIfCancellationRequested();
            string? error = ValidateReadItem(rows.GetValueOrDefault(request.Id), request.RowVersion);
            if (error is not null) { results.Add(new(request.Id, null, null, null, "RuleExtraction", [], error)); continue; }
            RuleParseResult rule = await ParseReviewItemAsync(rows[request.Id], roots, ct);
            bool conflict = rule.Conflicts is { Count: > 0 } || rule.RejectedFields?.Any(f => f is "season" or "episode" or "episodeEnd") == true;
            error = conflict ? "规则存在编号冲突或拒绝字段，请手动核对" : rule.Episode is null ? "规则无法确定单集编号，请手动填写" : null;
            results.Add(new(request.Id, conflict ? null : rule.Season, conflict ? null : rule.Episode,
                conflict ? null : rule.EpisodeEnd, "RuleExtraction", RuleEvidence(rule), error));
        }
        return new(results);
    }

    public async Task<ReviewLibraryCandidatesResult> LibraryCandidatesAsync(long mediaItemId, string? query, CancellationToken ct = default)
    {
        if (query?.Length > 100) throw new BusinessException("库内搜索词不能超过 100 字符");
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");
        // 仅已归档作品参加推荐；身份键始终同时含 movie/tv，推荐不写绑定。
        var files = await db.MediaItems.AsNoTracking().Where(m => m.Status == MediaItemStatus.Completed
                && m.TmdbId != null && (m.TmdbMediaType == "tv" || m.TmdbMediaType == "movie"))
            .OrderByDescending(m => m.Id).Take(20000)
            .Select(m => new { m.TmdbId, m.TmdbMediaType, m.ParsedInfo, m.SourcePath, m.ArchivedAt }).ToListAsync(ct);
        int[] tmdbIds = files.Select(f => f.TmdbId!.Value).Distinct().ToArray();
        List<MediaWork> works = await db.MediaWorks.AsNoTracking().Where(w => tmdbIds.Contains(w.TmdbId)).ToListAsync(ct);
        List<TmdbMetadataCache> caches = await db.TmdbMetadataCaches.AsNoTracking().Where(w => tmdbIds.Contains(w.TmdbId)).ToListAsync(ct);
        string clue = string.IsNullOrWhiteSpace(query) ? ParsedInfo.FromJson(item.ParsedInfo)?.Title ?? "" : query.Trim();
        string? directory = ParentDirectory(item.SourcePath);
        List<(ReviewLibraryCandidate Candidate, int Rank, double Score)> candidates = [];
        foreach (var group in files.GroupBy(f => (Id: f.TmdbId!.Value, Type: f.TmdbMediaType!)))
        {
            MediaWork? work = works.FirstOrDefault(w => w.TmdbId == group.Key.Id && w.MediaType == group.Key.Type);
            TmdbMetadataCache? cache = caches.FirstOrDefault(w => w.TmdbId == group.Key.Id && w.MediaType == group.Key.Type);
            var latest = group.OrderByDescending(f => f.ArchivedAt).First();
            ParsedInfo? parsed = ParsedInfo.FromJson(latest.ParsedInfo);
            string? title = work?.Title ?? cache?.Title ?? parsed?.Title;
            string? original = work?.OriginalTitle ?? cache?.OriginalTitle;
            double score = string.IsNullOrWhiteSpace(clue) ? 0 : Math.Max(TitleSimilarity.Ratio(clue, title), TitleSimilarity.Ratio(clue, original));
            bool contains = !string.IsNullOrWhiteSpace(clue) && new[] { title, original }.Any(t => t?.Contains(clue, StringComparison.OrdinalIgnoreCase) == true);
            bool bound = item.TmdbId == group.Key.Id && item.TmdbMediaType == group.Key.Type;
            bool sameDirectory = directory is not null && score >= 0.65 && group.Any(f => ParentDirectory(f.SourcePath) == directory);
            bool titleMatch = score >= 0.55 || contains;
            bool ongoing = group.Key.Type == "tv" && work?.TmdbStatus is "Returning Series" or "In Production" or "Planned";
            if (!bound && !sameDirectory && !titleMatch && (!string.IsNullOrWhiteSpace(query) || !ongoing)) continue;
            string source = bound || sameDirectory ? "LibraryContext" : titleMatch ? "LibraryTitle" : "LibraryRecent";
            string reason = bound ? "与当前绑定为同一类型及 TMDB ID" : sameDirectory ? "同一源目录且标题相近" : titleMatch ? "库内标题或原名相关" : "近期归档且 TMDB 标记为未完结";
            candidates.Add((new(group.Key.Id, group.Key.Type, title, original, work?.Year ?? cache?.Year ?? parsed?.Year,
                ToPosterUrl(work?.PosterPath ?? cache?.PosterPath), work?.TotalSeasons ?? cache?.TotalSeasons,
                source, reason, work?.TmdbStatus, latest.ArchivedAt), source == "LibraryContext" ? 0 : source == "LibraryTitle" ? 1 : 2, score));
        }
        return new(candidates.OrderBy(c => c.Rank).ThenByDescending(c => c.Score)
            .ThenByDescending(c => c.Candidate.LatestArchivedAt).Take(20).Select(c => c.Candidate).ToArray());
    }

    public async Task<ReviewEpisodeMappingResult> PreviewEpisodeMappingAsync(ReviewEpisodeMappingRequest req, CancellationToken ct = default)
    {
        if (req.TmdbId <= 0 || !string.Equals(req.MediaType, "tv", StringComparison.OrdinalIgnoreCase) || req.Season is < 2 or > 100)
            throw new BusinessException("请选择有效 TV 作品与第 2 至 100 季；特别篇不使用累计换算");
        if (req.Items is not { Count: > 0 and <= 100 }) throw new BusinessException("每次可预览 1 至 100 条记录");
        if (req.Items.Select(i => i.Id).Distinct().Count() != req.Items.Count) throw new BusinessException("映射预览不能包含重复记录");
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        long[] ids = req.Items.Select(i => i.Id).Distinct().ToArray();
        Dictionary<long, MediaItem> rows = await db.MediaItems.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        List<string> roots = await db.WatchFolders.AsNoTracking().Select(w => w.Path).ToListAsync(ct);
        // 先校验本地身份、原编号与明确季内语法，再消耗外部目录额度。
        Dictionary<long, (RuleParseResult? Rule, string? Error)> checkedRows = [];
        foreach (ReviewEpisodeMappingItem entry in req.Items)
        {
            string? error = ValidateReadItem(rows.GetValueOrDefault(entry.Id), entry.RowVersion);
            RuleParseResult? rule = null;
            if (error is null)
            {
                MediaItem item = rows[entry.Id];
                if (item.TmdbId is not null && (item.TmdbId != req.TmdbId || item.TmdbMediaType != "tv"))
                    error = "记录已绑定不同作品或类型，请先显式改绑或手动填写";
                else
                {
                    rule = await ParseReviewItemAsync(item, roots, ct);
                    bool explicitLocal = ExplicitSeasonEpisode.IsMatch(item.FileName)
                        || rule.NumberingEvidence?.Any(e => e.State == RuleEvidenceState.Accepted && e.Source == "FileName" && e.Kind == RuleNumberingKind.Season) == true;
                    bool special = rule.Season == 0 || rule.NumberingEvidence?.Any(e =>
                        e.Kind == RuleNumberingKind.ContentKind && e.State != RuleEvidenceState.Rejected
                        && e.TextValue?.ToUpperInvariant() is "OVA" or "OAD" or "SP" or "NCOP" or "NCED" or "PV" or "特别篇" or "特別篇" or "番外" or "特典") == true;
                    if (special) error = "源文件或目录属于特别篇/附加内容，不能纳入普通季累计换算";
                    else if (rule.NamingEvidence?.EditionTags is { Count: > 0 } || ParsedInfo.FromJson(item.ParsedInfo)?.OriginalEpisode is not null)
                        error = "源文件含未核实版本或既有编组映射，不能假定标准累计集序；请手动核对";
                    else if (explicitLocal) error = "源文件已明确标注季内季集，禁止自动减去前季集数；请手动核对";
                    else if (rule.Conflicts is { Count: > 0 } || rule.RejectedFields?.Any(f => f is "episode" or "episodeEnd") == true)
                        error = "源编号存在规则冲突，不能自动换算";
                    else if (!SourceNumberMatches(rule, entry.Episode, entry.EpisodeEnd))
                        error = "预览原编号与源文件提取结果不一致，请重新提取或手动填写";
                }
            }
            checkedRows[entry.Id] = (rule, error);
        }
        TmdbDetailsResult? details = null;
        List<TmdbSeasonCatalogueResult> catalogues = [];
        string? refreshError = null;
        if (checkedRows.Values.Any(r => r.Error is null))
        {
            details = await GetReviewDetailsAsync(req.TmdbId, "tv", req.ForceRefresh, ct);
            refreshError = details.RefreshError;
            if (Enumerable.Range(1, req.Season).All(n => details.Seasons?.Count(s => s.SeasonNumber == n && s.EpisodeCount > 0) == 1))
                for (int season = 1; season <= req.Season; season++)
                {
                    ct.ThrowIfCancellationRequested();
                    TmdbSeasonCatalogueResult? catalogue = await _tmdb.GetSeasonCatalogueAsync(req.TmdbId, season, req.ForceRefresh, ct);
                    if (catalogue is not null) { catalogues.Add(catalogue); refreshError ??= catalogue.RefreshError; }
                }
        }
        List<ReviewEpisodeMappingEntry> result = [];
        foreach (ReviewEpisodeMappingItem entry in req.Items)
        {
            (RuleParseResult? Rule, string? Error) local = checkedRows[entry.Id];
            ReviewEpisodeMappingGuard.Result? mapped = local.Error is null && details is not null
                ? ReviewEpisodeMappingGuard.Map(req.TmdbId, req.Season, entry.Episode, entry.EpisodeEnd, details, catalogues, DateTimeOffset.UtcNow) : null;
            string? error = local.Error ?? mapped?.Error;
            string? token = mapped?.Episode is int episode ? MappingToken(entry, rows[entry.Id], req.TmdbId, req.Season, episode, mapped.EpisodeEnd, mapped.CatalogueFingerprint!) : null;
            result.Add(new(entry.Id, local.Rule?.Season, entry.Episode, entry.EpisodeEnd,
                mapped?.Episode is null ? null : req.Season, mapped?.Episode, mapped?.EpisodeEnd,
                "AbsoluteMapping", (mapped?.Evidence ?? []).Concat(local.Rule is null ? [] : RuleEvidence(local.Rule))
                    .Concat(rows.TryGetValue(entry.Id, out MediaItem? source) && source.TmdbId is null
                        ? ["作品身份来自人工所选 TV 候选，未由源文件自动验证；本次仅预览"] : Array.Empty<string>()).ToArray(), error, token));
        }
        return new(result, catalogues.Count == 0 ? details?.CachedAt : catalogues.Min(c => c.CachedAt), refreshError);
    }

    private static bool SourceNumberMatches(RuleParseResult rule, int episode, int? end)
    {
        if (rule.Episode == episode && rule.EpisodeEnd == end) return true;
        if (rule.Episode is not null || end is not null) return false;
        int?[] absolute = (rule.NumberingEvidence ?? []).Where(e => e.Kind == RuleNumberingKind.Absolute
            && e.State is RuleEvidenceState.Accepted or RuleEvidenceState.Candidate && e.Source is "FileName" or "RelativeSegment")
            .Select(e => e.Value).Distinct().ToArray();
        return absolute.Length == 1 && absolute[0] == episode;
    }

    private static string MappingToken(ReviewEpisodeMappingItem item, MediaItem source, int tmdbId, int season, int episode, int? end, string fingerprint)
    {
        // 原路径只参与本地摘要，不向客户端或 TMDB 返回；令牌绑定源记录快照与所选目录。
        string snapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { source.SourcePath, source.FileName, source.ParsedInfo, source.TmdbId, source.TmdbMediaType }))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { item.Id, item.RowVersion, snapshot, tmdbId, mediaType = "tv", season, item.Episode, item.EpisodeEnd, targetEpisode = episode, targetEnd = end, fingerprint }))));
    }

    private async Task<RuleParseResult> ParseReviewItemAsync(MediaItem item, IReadOnlyList<string> roots, CancellationToken ct)
    {
        if (_rules is null) throw new BusinessException("规则提取服务不可用");
        string? root = roots.Where(r => IsWithinRoot(item.SourcePath, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        return await _rules.ParseAsync(FileParseContext.FromFullPath(item.SourcePath, root), ct);
    }
    private static bool IsWithinRoot(string path, string root)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static string? ParentDirectory(string path)
    {
        try { return Path.GetDirectoryName(path); } catch (ArgumentException) { return null; }
    }
    private static string? ValidateReadItem(MediaItem? item, long rowVersion)
        => item is null ? "记录不存在" : item.RowVersion != rowVersion ? "记录已被其他用户修改，请刷新"
            : item.Status != MediaItemStatus.AwaitingReview ? "该记录不在待确认状态" : null;
    private static IReadOnlyList<string> RuleEvidence(RuleParseResult rule)
        => (rule.NumberingEvidence ?? []).Where(e => e.Source is "FileName" or "RelativeSegment")
            .Select(e => $"{e.Source}/{e.Kind}/{e.State}：{(e.Token.Length > 120 ? e.Token[..120] : e.Token)}")
            .Concat(rule.Conflicts ?? []).Distinct().Take(20).ToArray();
}
