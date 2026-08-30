using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Scan;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.ParseTasks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Scan;

/// <summary>整理演练服务 — 只读预览解析/匹配/命名，不调 AI、不动文件、不写处理记录</summary>
/// <remarks>
/// 复用主管线同一批契约（IRuleEngineService / ITmdbSearchService / ParseTask / PlexNamingConventions），
/// 保证预览结果与真实处理一致；但在决策矩阵走到「触发 AI」时即停，不调用 IAiCallOrchestrator。
/// 候选阈值 / 四维打分权重与主管线同源（运行时读 Tmdb_Setting，缺行回退种子默认）；
/// 置信度阈值与得分门槛常量须与 ProcessFileService 保持一致（0.6 / 0.5 / 0.35）。
/// 副作用边界：TmdbSearchService.SearchAsync 会写 Tmdb_SearchCache（幂等、预热缓存，无害）；
/// 不创建/保存 MediaItem，不调 IArchiveService / IFileMover。
/// </remarks>
internal sealed class DryRunService : IDryRunService
{
    private const double ConfidenceThreshold = 0.6;
    private const int DefaultCandidateThreshold = 3;
    private const string FallbackExtension = "mkv";
    // 综合得分门槛：与 ProcessFileService.MultiCandidateMinScore / SingleCandidateMinScore 保持一致
    private const double MultiCandidateMinScore = 0.5;
    private const double SingleCandidateMinScore = 0.35;
    // 候选过多免 AI 裁决门槛：与 ProcessFileService.CrossCheckDominantScore / CrossCheckDominantGap 保持一致
    private const double CrossCheckDominantScore = 0.7;
    private const double CrossCheckDominantGap = 0.15;

    private readonly IRuleEngineService _ruleEngine;
    private readonly ITmdbSearchService _tmdb;
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly ILogger<DryRunService> _logger;

    public DryRunService(
        IRuleEngineService ruleEngine,
        ITmdbSearchService tmdb,
        IDbContextFactory<PmmDbContext> dbFactory,
        ILogger<DryRunService> logger)
    {
        _ruleEngine = ruleEngine;
        _tmdb = tmdb;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<DryRunPreview> PreviewAsync(string path, CancellationToken ct = default)
    {
        // 路径非空校验已上移至 DryRunRequest.Path DataAnnotations（[RequiredNotBlank]），由 [ApiController] 边界拦截。
        string normalized = path.Trim();
        string fileName = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(fileName))
            throw new BusinessException("无法从路径解析文件名");

        // TMDB 决策参数与主管线同源：候选阈值 N 与四维打分权重运行时读 Tmdb_Setting（设置页可调），
        // 单例行缺失回退种子默认值——保证演练预告与真实处理一致
        TmdbSetting? tmdbSetting;
        await using (PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct))
        {
            tmdbSetting = await db.TmdbSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        }
        int candidateThreshold = tmdbSetting?.CandidateThreshold ?? DefaultCandidateThreshold;
        TmdbScoreWeights scoreWeights = tmdbSetting is null
            ? TmdbScoreWeights.Default
            : new TmdbScoreWeights(
                tmdbSetting.ScoreWeightTitle, tmdbSetting.ScoreWeightYear,
                tmdbSetting.ScoreWeightPopularity, tmdbSetting.ScoreWeightLanguage);
        string preferredLanguage = tmdbSetting?.Language ?? "zh-CN";

        // 与「手动整理」一致：WatchFolderId=0 → watchRoot=null，退化为单层父目录上下文
        FileParseContext context = FileParseContext.FromFullPath(normalized, watchRoot: null);
        RuleParseResult rule = await _ruleEngine.ParseAsync(context, ct);

        // 决策一：规则置信度 / 特殊字符
        NextAction first = ParseTask
            .AfterRuleEngine(rule.Confidence, rule.HasSpecialChars, ConfidenceThreshold, candidateThreshold)
            .DecideAfterFirstTmdb();
        if (first == NextAction.CallAi)
        {
            string reason = rule.HasSpecialChars
                ? "命中特殊字符规则（中日韩混杂 / 罕见符号），实际处理会触发 AI 兜底"
                : $"规则置信度 {rule.Confidence:F2} < 阈值 {ConfidenceThreshold:F2}，实际处理会触发 AI 兜底";
            return Build(normalized, fileName, rule, DryRunOutcome.WouldCallAi, reason,
                tmdbQueried: false, candidates: Array.Empty<DryRunCandidate>(), picked: null,
                previewRel: null, previewNote: null);
        }

        // 走 TMDB（只读搜索；命中缓存不耗额度）。TMDB HTTP 异常包成 BusinessException(1000)，与
        // ReviewService.TmdbSearchAsync 一致——避免整理演练在 TMDB 限流/抖动时返 9000「服务器崩溃」语义。
        TmdbSearchResult tmdb;
        try
        {
            tmdb = await _tmdb.SearchAsync(
                new TmdbSearchRequest(rule.Title, rule.MediaType, rule.Year), ct);
        }
        catch (TmdbClientException ex)
        {
            throw new BusinessException("TMDB 服务异常", ex);
        }
        List<DryRunCandidate> candidates = tmdb.Candidates
            .Select(c => new DryRunCandidate(c.Id, c.MediaType, c.Title, c.OriginalTitle, c.Year, c.Popularity))
            .ToList();

        // 决策二：第一次 TMDB 候选数
        NextAction afterTmdb = ParseTask
            .AfterTmdbQuery(rule.Confidence, tmdb.Candidates.Count, rule.HasSpecialChars, ConfidenceThreshold, candidateThreshold)
            .DecideAfterFirstTmdb();
        // 候选过多免 AI 裁决（与主管线 ProcessFileService 同口径）：四维打分榜首显著（≥0.7 且领先次名 ≥0.15）
        // 直接按采纳预演；分差含糊才如实预告「会触发 AI / 备选交叉投票」（演练不重搜备选，避免额外查询开销）
        if (afterTmdb == NextAction.CallAi && tmdb.Candidates.Count > candidateThreshold)
        {
            IReadOnlyList<TmdbCandidateScore> preRanked = TmdbCandidateScorer.Rank(
                tmdb.Candidates, [rule.Title], rule.Year, scoreWeights, preferredLanguage);
            double gap = preRanked.Count > 1 ? preRanked[0].Score - preRanked[1].Score : double.MaxValue;
            if (preRanked[0].Score >= CrossCheckDominantScore && gap >= CrossCheckDominantGap)
            {
                afterTmdb = NextAction.UseTmdb;
            }
        }
        if (afterTmdb == NextAction.CallAi)
        {
            string reason = tmdb.Candidates.Count == 0
                ? "TMDB 零候选，实际处理会先做本地备选重搜，仍全零且规则高置信则判定 TMDB 未收录转人工（每日自动重试），否则触发 AI 兜底"
                : $"TMDB 候选 {tmdb.Candidates.Count} 个（> 阈值 {candidateThreshold}）且打分无显著领先，实际处理会先做备选标题交叉投票，未果才触发 AI 兜底";
            return Build(normalized, fileName, rule, DryRunOutcome.WouldCallAi, reason,
                tmdbQueried: true, candidates, picked: null, previewRel: null, previewNote: null);
        }

        // 四维加权择优（与主管线 ProcessFileService 同口径）：按 标题相似度/年份差/热度/语言产地 重排候选，
        // 最佳放首位；最高分低于门槛（多候选 0.5 / 单候选 0.35）→ 实际处理会转人工审核，演练如实预告
        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(
            tmdb.Candidates, [rule.Title], rule.Year, scoreWeights, preferredLanguage);
        tmdb = tmdb with { Candidates = ranked.Select(r => r.Candidate).ToList() };
        candidates = tmdb.Candidates
            .Select(c => new DryRunCandidate(c.Id, c.MediaType, c.Title, c.OriginalTitle, c.Year, c.Popularity))
            .ToList();
        double topScore = ranked[0].Score;
        double minScore = tmdb.Candidates.Count > 1 ? MultiCandidateMinScore : SingleCandidateMinScore;
        if (topScore < minScore)
        {
            return Build(normalized, fileName, rule, DryRunOutcome.WouldReview,
                $"TMDB 候选最高综合得分 {topScore:F2} 低于门槛 {minScore:F2}（四维加权：标题/年份/热度/语言），实际会转人工审核",
                tmdbQueried: true, candidates, picked: null, previewRel: null, previewNote: "候选综合得分低于门槛");
        }

        // 采纳综合得分最高候选（候选数 ∈ [1, N]）
        TmdbCandidate top = tmdb.Candidates[0];
        DryRunCandidate picked = candidates[0];
        bool isTv = string.Equals(top.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        // 标题段语言优先级（与实际归档一致）：TMDB 候选中文名 → TMDB 候选原文名 → 规则解析名
        string title = ArchiveFolderResolver.FirstNonBlank(top.Title, top.OriginalTitle, rule.Title) ?? rule.Title;
        int? year = rule.Year ?? top.Year;  // 剧集常无 rule.year，用 TMDB 候选兜底
        int? season = rule.Season;
        int? episode = rule.Episode;

        if (year is null)
        {
            return Build(normalized, fileName, rule, DryRunOutcome.WouldReview,
                "命中 TMDB 候选，但年份缺失，无法按 Plex 命名，实际会转人工补全",
                tmdbQueried: true, candidates, picked, previewRel: null, previewNote: "年份缺失");
        }
        if (isTv && (season is null || episode is null))
        {
            return Build(normalized, fileName, rule, DryRunOutcome.WouldReview,
                $"命中 TMDB 剧集候选，但季 / 集字段不全（season={season?.ToString() ?? "?"} / episode={episode?.ToString() ?? "?"}），实际会转人工补全",
                tmdbQueried: true, candidates, picked, previewRel: null, previewNote: "剧集季 / 集不全");
        }

        string ext = Path.GetExtension(normalized).TrimStart('.');
        bool extFallback = string.IsNullOrEmpty(ext);
        if (extFallback) ext = FallbackExtension;

        // 与实际归档共用 ArchiveFolderResolver：演练无分类根（不含分类决策），传 null 跳过 {tmdb-NNN} 复用，仅做标题规范化
        string previewRel;
        try
        {
            // 多季：演练无 TMDB 详情缓存（rawJson），季年份取不到回退整剧年；季标题仅靠文件解析篇章名 rule.SeasonTitle
            (_, string? dryRunSeasonTitle) = isTv
                ? ArchiveFolderResolver.ResolveSeasonNaming(null, rule.SeasonTitle, season ?? 0)
                : (null, null);
            previewRel = ArchiveFolderResolver.BuildRelativeTargetPath(
                title, year.Value, top.Id, isTv, season ?? 0, episode ?? 0, rule.EpisodeEnd, ext,
                targetRootForReuse: null, logger: null, seasonYear: null, seasonTitle: dryRunSeasonTitle);
        }
        catch (ArgumentException ex)
        {
            // 标题清洗后为空 / 年份越界等命名校验失败 → 实际处理也会失败转人工
            return Build(normalized, fileName, rule, DryRunOutcome.WouldReview,
                $"命中 TMDB 候选，但命名校验未通过：{ex.Message}",
                tmdbQueried: true, candidates, picked, previewRel: null, previewNote: "命名校验未通过");
        }

        string note = extFallback
            ? $"路径无扩展名，已用 .{FallbackExtension} 占位预览；最终归档根目录由分类规则确定（演练不含分类决策）"
            : "最终归档根目录由分类规则确定（演练不含分类决策）";

        _logger.LogDebug("整理演练：{Path} → {Outcome} ({Preview})", normalized, DryRunOutcome.WouldArchive, previewRel);
        return Build(normalized, fileName, rule, DryRunOutcome.WouldArchive,
            $"规则 + TMDB 命中候选 {tmdb.Candidates.Count} 个（≤ 阈值 {candidateThreshold}），按四维加权取最高分 {topScore:F2}",
            tmdbQueried: true, candidates, picked, previewRel, note);
    }

    private static DryRunPreview Build(
        string path, string fileName, RuleParseResult rule,
        DryRunOutcome outcome, string reason,
        bool tmdbQueried, IReadOnlyList<DryRunCandidate> candidates, DryRunCandidate? picked,
        string? previewRel, string? previewNote)
        => new(
            path, fileName,
            rule.Title, rule.Year, rule.MediaType, rule.Season, rule.Episode, rule.EpisodeEnd,
            rule.Confidence, rule.HasSpecialChars, rule.MatchedRuleId,
            outcome, reason,
            tmdbQueried, candidates, picked,
            previewRel, previewNote);
}
