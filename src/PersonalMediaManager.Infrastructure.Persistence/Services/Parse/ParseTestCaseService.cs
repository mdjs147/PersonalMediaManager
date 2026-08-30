using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.ParseTestCases;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>Parse_TestCase 服务实现（PR2：CRUD + 暂存区灌入 + 状态流转；PR3：RunSingle/RunBatch/ApproveAsExpected）</summary>
/// <remarks>
/// 路径反查 WatchRootPath 复用 HistoryService 的「目录段边界 + 最长前缀」算法（避免 F:\迅雷 误命中 F:\迅雷下载\xxx）。
/// ImportFromFailed 不拷贝 Failed 的 ParsedInfo 到 Expected* — Failed 解析本身有问题，拷过来反而误导期望基线。
/// 回归运行复用 IRuleEngineService.ParseAsync 跑同一套规则引擎（避免规则逻辑发散）；LastRunResult JSON 只存实测，
/// 字段级 Diff 视觉呈现由前端按 Expected vs LastRunResult 实时比较。
/// </remarks>
internal sealed class ParseTestCaseService : IParseTestCaseService
{
    private const int MaxImportLimit = 200;
    private const int MaxBatchRunLimit = 500;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private const int TriageExistingSampleLimit = 30;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IAiAdvisor _aiAdvisor;

    public ParseTestCaseService(IDbContextFactory<PmmDbContext> dbFactory, IRuleEngineService ruleEngine, IAiAdvisor aiAdvisor)
    {
        _dbFactory = dbFactory;
        _ruleEngine = ruleEngine;
        _aiAdvisor = aiAdvisor;
    }

    public async Task<ParseTestCaseListResponse> ListAsync(ParseTestCaseListQuery query, CancellationToken ct = default)
    {
        int page = query.Page <= 0 ? 1 : query.Page;
        int pageSize = query.PageSize switch
        {
            <= 0 => 50,
            > 200 => 200,
            _ => query.PageSize,
        };

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<ParseTestCase> q = ctx.ParseTestCases.AsNoTracking();

        if (query.Status.HasValue)
            q = q.Where(x => x.Status == query.Status.Value);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string kw = query.Keyword.Trim();
            string pattern = $"%{kw}%";
            q = q.Where(x =>
                EF.Functions.Like(x.SamplePath, pattern)
                || (x.ExpectedTitle != null && EF.Functions.Like(x.ExpectedTitle, pattern)));
        }

        int total = await q.CountAsync(ct);
        List<ParseTestCase> rows = await q
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new ParseTestCaseListResponse(total, page, pageSize, rows.Select(ToResponse).ToList());
    }

    public async Task<ParseTestCaseResponse> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={id} 不存在");
        return ToResponse(entity);
    }

    public async Task<ParseTestCaseResponse> CreateAsync(CreateParseTestCaseRequest req, CancellationToken ct = default)
    {
        // A 类无状态字段校验（SamplePath/WatchRootPath/ExpectedTitle/Notes 非空与长度、ExpectedYear/Season/Episode 范围）
        // 已上移 DTO DataAnnotations；此处仅做 Trim 归一 + 保留「ExpectedMediaType≠Both」排除式枚举校验。
        string samplePath = NormalizeSamplePath(req.SamplePath);
        string? watchRoot = NormalizeOptionalPath(req.WatchRootPath);
        ValidateExpectedMediaType(req.ExpectedMediaType);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.ParseTestCases.AnyAsync(x => x.SamplePath == samplePath, ct))
            throw new BusinessException($"样本路径 {samplePath} 已存在");

        // 手动新增未填 WatchRootPath 时反查监控目录（最长前缀），命中则填，未命中保持 null
        if (string.IsNullOrWhiteSpace(watchRoot))
        {
            watchRoot = await TryResolveWatchRootAsync(samplePath, ctx, ct);
        }

        ParseTestCase entity = new()
        {
            Source = ParseTestCaseSource.Manual,
            Status = ParseTestCaseStatus.Active,
            SamplePath = samplePath,
            WatchRootPath = string.IsNullOrWhiteSpace(watchRoot) ? null : watchRoot,
            ExpectedTitle = req.ExpectedTitle?.Trim(),
            ExpectedYear = req.ExpectedYear,
            ExpectedMediaType = req.ExpectedMediaType,
            ExpectedSeason = req.ExpectedSeason,
            ExpectedEpisode = req.ExpectedEpisode,
            Notes = req.Notes?.Trim(),
        };
        ctx.ParseTestCases.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<ParseTestCaseResponse> UpdateAsync(UpdateParseTestCaseRequest req, CancellationToken ct = default)
    {
        // A 类无状态字段校验同 CreateAsync 已上移 DTO；此处仅 Trim 归一 + ExpectedMediaType≠Both 校验。
        string samplePath = NormalizeSamplePath(req.SamplePath);
        string? watchRoot = NormalizeOptionalPath(req.WatchRootPath);
        ValidateExpectedMediaType(req.ExpectedMediaType);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"测试用例 Id={req.Id} 已被其他操作修改，请刷新后重试");

        if (entity.SamplePath != samplePath
            && await ctx.ParseTestCases.AnyAsync(x => x.Id != req.Id && x.SamplePath == samplePath, ct))
            throw new BusinessException($"样本路径 {samplePath} 已存在");

        entity.SamplePath = samplePath;
        entity.WatchRootPath = string.IsNullOrWhiteSpace(watchRoot) ? null : watchRoot;
        entity.ExpectedTitle = req.ExpectedTitle?.Trim();
        entity.ExpectedYear = req.ExpectedYear;
        entity.ExpectedMediaType = req.ExpectedMediaType;
        entity.ExpectedSeason = req.ExpectedSeason;
        entity.ExpectedEpisode = req.ExpectedEpisode;
        entity.Notes = req.Notes?.Trim();
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteParseTestCaseRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        ctx.ParseTestCases.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<ImportFromFailedResult> ImportFromFailedAsync(ImportFromFailedRequest req, CancellationToken ct = default)
    {
        int limit = req.Limit switch
        {
            <= 0 => 50,
            > MaxImportLimit => MaxImportLimit,
            _ => req.Limit,
        };

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        IQueryable<MediaItem> failedQ = ctx.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.Failed);
        if (req.SinceUtc.HasValue)
            failedQ = failedQ.Where(m => m.LastAttemptAt >= req.SinceUtc.Value);

        List<MediaItem> candidates = await failedQ
            .OrderByDescending(m => m.LastAttemptAt)
            .ThenByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new ImportFromFailedResult(0, 0, Array.Empty<ImportFromFailedFailure>());

        HashSet<string> existing = new(
            await ctx.ParseTestCases
                .Where(x => candidates.Select(c => c.SourcePath).Contains(x.SamplePath))
                .Select(x => x.SamplePath)
                .ToListAsync(ct),
            StringComparer.Ordinal);

        int imported = 0;
        int skipped = 0;
        List<ImportFromFailedFailure> failures = new();
        foreach (MediaItem item in candidates)
        {
            try
            {
                if (existing.Contains(item.SourcePath))
                {
                    skipped++;
                    continue;
                }

                string? watchRoot = await TryResolveWatchRootAsync(item.SourcePath, ctx, ct);

                ctx.ParseTestCases.Add(new ParseTestCase
                {
                    Source = ParseTestCaseSource.FromFailed,
                    Status = ParseTestCaseStatus.PendingTriage,
                    SamplePath = item.SourcePath,
                    WatchRootPath = string.IsNullOrWhiteSpace(watchRoot) ? null : watchRoot,
                    Notes = string.IsNullOrWhiteSpace(item.ErrorMessage)
                        ? $"灌入自 Media_Item Id={item.Id}"
                        : $"灌入自 Media_Item Id={item.Id}；失败原因：{Truncate(item.ErrorMessage, 300)}",
                });
                existing.Add(item.SourcePath);
                imported++;
            }
            catch (BusinessException ex)
            {
                failures.Add(new ImportFromFailedFailure(item.Id, item.SourcePath, ex.Message));
            }
        }

        if (imported > 0) await ctx.SaveChangesAsync(ct);
        return new ImportFromFailedResult(imported, skipped, failures);
    }

    public Task<ParseTestCaseResponse> PromoteToActiveAsync(TransitionParseTestCaseStatusRequest req, CancellationToken ct = default) =>
        TransitionAsync(req, ParseTestCaseStatus.Active, allowedFrom: new[] { ParseTestCaseStatus.PendingTriage, ParseTestCaseStatus.Disabled }, ct);

    public Task<ParseTestCaseResponse> DisableAsync(TransitionParseTestCaseStatusRequest req, CancellationToken ct = default) =>
        TransitionAsync(req, ParseTestCaseStatus.Disabled, allowedFrom: new[] { ParseTestCaseStatus.PendingTriage, ParseTestCaseStatus.Active }, ct);

    public Task<ParseTestCaseResponse> ResetToTriageAsync(TransitionParseTestCaseStatusRequest req, CancellationToken ct = default) =>
        TransitionAsync(req, ParseTestCaseStatus.PendingTriage, allowedFrom: new[] { ParseTestCaseStatus.Active, ParseTestCaseStatus.Disabled }, ct);

    private async Task<ParseTestCaseResponse> TransitionAsync(
        TransitionParseTestCaseStatusRequest req,
        ParseTestCaseStatus target,
        ParseTestCaseStatus[] allowedFrom,
        CancellationToken ct)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"测试用例 Id={req.Id} 已被其他操作修改，请刷新后重试");

        if (entity.Status == target)
            throw new BusinessException($"测试用例已处于 {target} 状态，无需切换");

        if (Array.IndexOf(allowedFrom, entity.Status) < 0)
            throw new BusinessException($"状态切换非法：{entity.Status} → {target}");

        entity.Status = target;
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<ParseTestCaseResponse> RunAsync(RunParseTestCaseRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        await RunOneAsync(entity, ct);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<RunParseTestCasesBatchResult> RunBatchAsync(RunParseTestCasesBatchRequest req, CancellationToken ct = default)
    {
        int limit = req.Limit switch
        {
            <= 0 => 200,
            > MaxBatchRunLimit => MaxBatchRunLimit,
            _ => req.Limit,
        };

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<ParseTestCase> q = ctx.ParseTestCases.AsQueryable();
        if (req.Status.HasValue)
            q = q.Where(x => x.Status == req.Status.Value);
        List<ParseTestCase> rows = await q
            .OrderBy(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        int ran = 0;
        int pass = 0;
        int fail = 0;
        int notComparable = 0;
        List<RunBatchFailure> failures = new();
        foreach (ParseTestCase entity in rows)
        {
            try
            {
                ParseTestCaseRunStatus status = await RunOneAsync(entity, ct);
                ran++;
                switch (status)
                {
                    case ParseTestCaseRunStatus.Pass: pass++; break;
                    case ParseTestCaseRunStatus.Fail: fail++; break;
                    default: notComparable++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new RunBatchFailure(entity.Id, entity.SamplePath, ex.Message));
            }
        }

        if (ran > 0) await ctx.SaveChangesAsync(ct);
        return new RunParseTestCasesBatchResult(ran, pass, fail, notComparable, failures);
    }

    public async Task<ParseTestCaseResponse> ApproveAsExpectedAsync(ApproveAsExpectedRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"测试用例 Id={req.Id} 已被其他操作修改，请刷新后重试");

        if (string.IsNullOrWhiteSpace(entity.LastRunResult))
            throw new BusinessException("用例尚未运行回归，无法批准基线；请先「运行」一次再批准");

        ParsedActual actual;
        try
        {
            actual = ParseLastRunResultJson(entity.LastRunResult);
        }
        catch (JsonException ex)
        {
            throw new BusinessException($"LastRunResult JSON 损坏，无法批准：{ex.Message}");
        }

        entity.ExpectedTitle = actual.Title;
        entity.ExpectedYear = actual.Year;
        entity.ExpectedMediaType = actual.MediaType;
        entity.ExpectedSeason = actual.Season;
        entity.ExpectedEpisode = actual.Episode;
        // 拷贝后期望就等于实测，必然 Pass
        entity.LastRunStatus = ParseTestCaseRunStatus.Pass;
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<ParseTestCaseResponse> TriageWithAiAsync(TriageWithAiRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        // 取现有样本路径供 AI 判重（排除自己）
        List<string> existing = await ctx.ParseTestCases.AsNoTracking()
            .Where(x => x.Id != entity.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.SamplePath)
            .Take(TriageExistingSampleLimit)
            .ToListAsync(ct);

        TriageAdvice advice = await _aiAdvisor.TriageAsync(
            new TriageInput(entity.SamplePath, entity.WatchRootPath, entity.Notes, existing), ct);

        entity.AiVerdict = JsonSerializer.Serialize(new
        {
            worthAdding = advice.WorthAdding,
            reason = advice.Reason,
            similarity = advice.SimilarityToExisting,
            suggested = new
            {
                title = advice.SuggestedTitle,
                year = advice.SuggestedYear,
                mediaType = advice.SuggestedMediaType?.ToString(),
                season = advice.SuggestedSeason,
                episode = advice.SuggestedEpisode,
            },
            advisedAt = DateTimeOffset.UtcNow,
        }, JsonOpts);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<RuleSuggestionResponse> SuggestRuleAsync(SuggestRuleRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseTestCase? entity = await ctx.ParseTestCases.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"测试用例 Id={req.Id} 不存在");

        RuleSuggestion suggestion = await _aiAdvisor.SuggestRuleAsync(
            new SuggestRuleInput(
                entity.SamplePath,
                entity.WatchRootPath,
                entity.ExpectedTitle,
                entity.ExpectedYear,
                entity.ExpectedMediaType,
                entity.ExpectedSeason,
                entity.ExpectedEpisode),
            ct);

        // 落 pattern 作为「AI 已建议过规则」痕迹（scope/explanation 仅随响应返前端，不落库）
        entity.AiSuggestedRulePattern = Truncate(suggestion.Pattern, 1000);
        await ctx.SaveChangesAsync(ct);

        return new RuleSuggestionResponse(
            entity.Id,
            suggestion.Pattern,
            suggestion.Scope,
            suggestion.DefaultType,
            suggestion.Explanation);
    }

    /// <summary>跑一次回归 + 写 LastRun*；返回比对结果（Pass / Fail / NotRun 表示无可比基线）</summary>
    /// <remarks>
    /// 不调 SaveChanges（让外层批量场景可以攒一次性提交）。
    /// 无任何 Expected 字段非空 → 视为「基线未设」→ LastRunStatus = NotRun（已跑但无法评判）。
    /// </remarks>
    private async Task<ParseTestCaseRunStatus> RunOneAsync(ParseTestCase entity, CancellationToken ct)
    {
        FileParseContext context = FileParseContext.FromFullPath(entity.SamplePath, entity.WatchRootPath);
        RuleParseResult result = await _ruleEngine.ParseAsync(context, ct);

        entity.LastRunAt = DateTimeOffset.UtcNow;
        entity.LastMatchedRuleId = result.MatchedRuleId;
        entity.LastRunResult = JsonSerializer.Serialize(new
        {
            title = result.Title,
            year = result.Year,
            mediaType = result.MediaType,
            season = result.Season,
            episode = result.Episode,
            episodeEnd = result.EpisodeEnd,
            confidence = result.Confidence,
            hasSpecialChars = result.HasSpecialChars,
        }, JsonOpts);

        ParseTestCaseRunStatus status = CompareWithExpected(entity, result);
        entity.LastRunStatus = status;
        return status;
    }

    private static ParseTestCaseRunStatus CompareWithExpected(ParseTestCase entity, RuleParseResult result)
    {
        bool anyExpected =
            entity.ExpectedTitle is not null
            || entity.ExpectedYear is not null
            || entity.ExpectedMediaType is not null
            || entity.ExpectedSeason is not null
            || entity.ExpectedEpisode is not null;
        if (!anyExpected)
            return ParseTestCaseRunStatus.NotRun;

        if (entity.ExpectedTitle is not null
            && !string.Equals(entity.ExpectedTitle.Trim(), result.Title?.Trim(), StringComparison.OrdinalIgnoreCase))
            return ParseTestCaseRunStatus.Fail;

        if (entity.ExpectedYear is not null && entity.ExpectedYear != result.Year)
            return ParseTestCaseRunStatus.Fail;

        if (entity.ExpectedMediaType is not null)
        {
            string expected = entity.ExpectedMediaType.Value.ToString().ToLowerInvariant();
            if (!string.Equals(expected, result.MediaType?.ToLowerInvariant(), StringComparison.Ordinal))
                return ParseTestCaseRunStatus.Fail;
        }

        if (entity.ExpectedSeason is not null && entity.ExpectedSeason != result.Season)
            return ParseTestCaseRunStatus.Fail;

        if (entity.ExpectedEpisode is not null && entity.ExpectedEpisode != result.Episode)
            return ParseTestCaseRunStatus.Fail;

        return ParseTestCaseRunStatus.Pass;
    }

    private readonly record struct ParsedActual(string? Title, int? Year, MediaType? MediaType, int? Season, int? Episode);

    /// <summary>解析 LastRunResult JSON 取标题/年/类型/季/集；类型字段统一映射到 MediaType 枚举（movie/tv → Movie/Tv）</summary>
    private static ParsedActual ParseLastRunResultJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string? title = ReadString(root, "title");
        int? year = ReadInt(root, "year");
        int? season = ReadInt(root, "season");
        int? episode = ReadInt(root, "episode");
        string? mediaTypeRaw = ReadString(root, "mediaType");
        MediaType? mediaType = mediaTypeRaw?.ToLowerInvariant() switch
        {
            "movie" => Domain.Enums.MediaType.Movie,
            "tv" => Domain.Enums.MediaType.Tv,
            _ => null,
        };
        return new ParsedActual(title, year, mediaType, season, episode);

        static string? ReadString(JsonElement r, string name) =>
            r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? ReadInt(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out JsonElement v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int iv)) return iv;
            return null;
        }
    }

    private static ParseTestCaseResponse ToResponse(ParseTestCase x) =>
        new(
            x.Id,
            x.Source,
            x.Status,
            x.SamplePath,
            x.WatchRootPath,
            x.ExpectedTitle,
            x.ExpectedYear,
            x.ExpectedMediaType,
            x.ExpectedSeason,
            x.ExpectedEpisode,
            x.LastRunAt,
            x.LastRunStatus,
            x.LastRunResult,
            x.LastMatchedRuleId,
            x.AiVerdict,
            x.AiSuggestedRulePattern,
            x.Notes,
            x.RowVersion,
            x.CreatedAt,
            x.UpdatedAt);

    // 非空与长度校验已上移 DTO（[RequiredNotBlank]/[MaxLength(1000)]），本方法仅保留 Trim 归一
    private static string NormalizeSamplePath(string raw) => raw.Trim();

    // 长度校验已上移 DTO（[MaxLength(1000)]）；本方法保留「空白归一为 null + Trim」语义
    private static string? NormalizeOptionalPath(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    // ExpectedTitle/Year/Season/Episode 的长度与范围校验已上移 DTO；
    // 仅保留「ExpectedMediaType≠Both」——排除式枚举判定（非声明式取值集），KEEP 在 service
    private static void ValidateExpectedMediaType(MediaType? mediaType)
    {
        if (mediaType == MediaType.Both)
            throw new BusinessException("ExpectedMediaType 不允许 Both（仅 Movie / Tv）");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    /// <summary>按 SamplePath 反查 WatchFolder.Path（目录段边界 + 最长前缀）；找不到返 null</summary>
    /// <remarks>
    /// 算法与 HistoryService.TryResolveWatchFolderIdAsync 一致：
    /// 拼上 DirectorySeparatorChar / AltDirectorySeparatorChar 再 StartsWith，避免 F:\迅雷 误命中 F:\迅雷下载\xxx.mp4。
    /// 不过滤 Enabled：禁用只代表停了 Watcher，路径归属语义不变。
    /// </remarks>
    private static async Task<string?> TryResolveWatchRootAsync(string samplePath, PmmDbContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(samplePath)) return null;

        var rows = await ctx.WatchFolders.AsNoTracking()
            .Where(w => w.Path != null && w.Path != "")
            .Select(w => new { w.Path })
            .ToListAsync(ct);

        string? best = null;
        int bestLen = -1;
        foreach (var row in rows)
        {
            string root = row.Path!.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (root.Length == 0) continue;

            bool hit =
                samplePath.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                samplePath.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (hit && root.Length > bestLen)
            {
                best = root;
                bestLen = root.Length;
            }
        }
        return best;
    }
}
