using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>规则引擎服务（D7.2）— 用户规则按优先级 → 内置规则兜底</summary>
/// <remarks>
/// 需求文档 §3.3.1 完整实现：噪声清洗 + 季集模式 + 年份提取 + 类型推断 + 父目录上下文加成 + 置信度评分表。
/// 用户规则（Parse_Rule）按 Priority 升序 + Enabled=true，命名捕获 title/year/season/episode；
/// 一条用户规则命中即返回（先命中先采用）；零命中走内置规则。
///
/// 正则安全：所有正则强制 RegexOptions.Compiled + MatchTimeout=500ms 防 ReDoS（需求文档 §3.3.1）；
/// 编译失败或匹配超时会保留诊断并进入审核；规则计算总预算 2 秒，轨迹与缓存均有容量上限。
///
/// 置信度评分表（需求文档 §3.3.1）：
///   title+type+season+episode+year → 0.95
///   title+type+season+episode（无 year） → 0.85
///   title+type+year（电影无季集） → 0.85
///   title+type（缺季集或年份） → 0.70
///   仅 title → 0.50
///   啥都没有 → 0.10
///
/// 剧集硬约束（按缺失字段分档）：
///   tv 缺 episode → 0.50 触发 AI 兜底——集号无法靠 TMDB 反推，让 AI 从父目录 / 全路径段重新挖；
///   tv 仅缺 season（episode 在）→ 0.70 走 TMDB 直查——下游「单季自动补季」自动定 S01、
///   多季剧转人工审核选季，均优于为一个大概率是 1 的季号动用 AI。
///   Plex 命名规范要求剧集必有 SxxExx（ArchiveService 缺字段会抛 BusinessException），
///   两档取值都保证缺口在归档前由 AI / 自动补季 / 人工审核补全，不会直接 Failed。
///
/// 特殊字符判定：标题既有 CJK 又有拉丁字符（各 ≥ 3 个字符）→ HasSpecialChars=true，
/// 上层 ProcessFileService 据此触发 §3.3.2「直接走 AI」分支。
/// </remarks>
internal sealed partial class RuleEngineService : IRuleEngineService
{
    // partial 文件的字段初始化顺序不保证；超时用属性避免新正则先读到 TimeSpan.Zero。
    private static TimeSpan RegexTimeout => TimeSpan.FromMilliseconds(500);
    private const RegexOptions BaseOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // 内置正则全部下沉到 BuiltinRulesCatalog 作为唯一来源，
    // 该 catalog 同时供 /parse-rules/builtin 端点把规则透出到 UI 让用户感知到内置规则的存在。

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly ILogger<RuleEngineService> _logger;

    public RuleEngineService(IDbContextFactory<PmmDbContext> dbFactory, ILogger<RuleEngineService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<RuleParseResult> ParseAsync(FileParseContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<ParseRule> userRules = await db.ParseRules.AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);

        using RuleRegexExecution.Run execution = RuleRegexExecution.Begin(ct);
        RuleParseResult? result = null;
        try
        {
            foreach (ParseRule rule in userRules)
            {
                if (!execution.CanContinue()) break;
                execution.RulesEvaluated++;
                result = TryUserRule(rule, context, execution);
                if (result is null) continue;
                result = SupplementUserRule(result, context);
                break;
            }
            if (result is null && execution.CanContinue()) result = ApplyBuiltinRules(context);
            result ??= IncompleteFallback(context);
            if (execution.CanContinue()) result = ApplyStructuredEvidence(FinalizeNaming(result, context), context);
        }
        catch (RegexMatchTimeoutException)
        {
            execution.Incomplete("UnwrappedRegexTimeout");
            result ??= IncompleteFallback(context);
        }
        return execution.Complete(result);
    }

    private static RuleParseResult IncompleteFallback(FileParseContext context) =>
        new(Path.GetFileNameWithoutExtension(context.FileName), null, "unknown", null, null, null,
            0.1, false, null, HasIdentityEvidence: false);

    // ---------- 用户规则 ----------

    private RuleParseResult? TryUserRule(ParseRule rule, FileParseContext context, RuleRegexExecution.Run execution)
    {
        string hash = RuleRegexExecution.PatternHash(rule.Pattern);
        string key = $"user:{rule.Id}:{hash[..12]}";
        string scope = rule.Scope.ToString();
        execution.Trace(new(key, "attempt", RuleId: rule.Id, Scope: scope, PatternHash: hash));
        long started = Stopwatch.GetTimestamp();
        Regex re;
        try
        {
            re = RuleRegexExecution.Get(rule.Pattern);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("用户规则正则非法（跳过）：RuleId={RuleId} PatternHash={PatternHash}", rule.Id, hash);
            execution.Incomplete("InvalidRulePattern");
            execution.Trace(new(key, "rejected", "InvalidPattern", rule.Id, scope, PatternHash: hash));
            return null;
        }

        // 按 scope 枚举候选 input（AllAncestors 多 input 内→外逐段尝试，其他 scope 单 input）
        Match? matched = null;
        string matchedInput = string.Empty;
        int inputIndex = -1;
        int? matchedSegment = null;
        string matchedSource = scope;
        foreach (string input in EnumerateInputs(rule.Scope, context))
        {
            inputIndex++;
            if (!execution.CanContinue()) return null;
            if (string.IsNullOrEmpty(input)) continue;
            string inputSource = scope is "FileName" || scope is "AllAncestors" && inputIndex == 0 ? "FileName"
                : scope is "ParentFolder" or "AllAncestors" ? "RelativeSegment" : scope;
            int? inputSegment = inputSource == "RelativeSegment"
                ? context.RelativeSegments.Count - (scope == "ParentFolder" ? 1 : inputIndex) : null;
            int faults = execution.FaultCount;
            Match match = SafeMatch(re, input);
            if (execution.FaultCount != faults)
            {
                execution.Trace(new(key, "rejected", execution.LastIssue, rule.Id, scope, inputSource, inputSegment, hash));
                return null;
            }
            if (!match.Success) continue;
            if (!HasEffectiveCapture(match, input, rule))
            {
                execution.Trace(new(key, "rejected", "NoEffectiveCapture", rule.Id, scope, inputSource, inputSegment, hash));
                continue;
            }
            matched = match;
            matchedInput = input;
            matchedSource = inputSource;
            matchedSegment = inputSegment;
            break;
        }
        if (matched is null)
        {
            execution.Trace(new(key, "noMatch", RuleId: rule.Id, Scope: scope,
                PatternHash: hash, ElapsedMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return null;
        }

        string fileName = context.FileName;
        string? parentFolderName = context.DirectParentFolderName;
        Match capt = matched;

        string? title = TryGroup(capt, "title");
        int? year = TryParseInt(TryGroup(capt, "year"));
        if (year is int capturedYear && !MediaYearEvidence.ContainsYear([matchedInput], capturedYear)) year = null;
        int? season = ParseCjkSeason(TryGroup(capt, "season"));
        string? seasonTitle = TryGroup(capt, "seasonTitle");
        int? episode = TryParseInt(TryGroup(capt, "episode"));
        int? episodeEnd = TryParseInt(TryGroup(capt, "episodeEnd"));
        // 范围非法（end < start）→ 丢弃 end 字段，保留 start 单值
        if (episodeEnd is int e && episode is int s && e < s) episodeEnd = null;
        // 用户规则同样做小数集守护（与内置提取器口径一致，检测逻辑同源）：最后一个集号捕获组末尾
        // 在原文中紧跟 .数字（总集篇/回顾篇，如「番名 - 11.5 [1080p]」）说明正则截走了小数集的整数
        // 部分，丢弃集号转低置信走 AI/审核，防止特别篇顶替正片集号；.1080p 多位数字、.5v2 数字后
        // 跟字母、年份形态均不误判。改在提取结果层而非种子 Pattern：给 Pattern 加负向断言会因
        // title 懒惰组回溯吞掉「11.」反而产出 episode=5，且结果层守护对用户自定义规则一并生效。
        bool fractionalEpisodeRejected = false;
        if (episode is not null)
        {
            Group lastDigits = capt.Groups["episodeEnd"].Success ? capt.Groups["episodeEnd"] : capt.Groups["episode"];
            if (HasFractionalEpisodeTail(matchedInput, lastDigits.Index + lastDigits.Length))
            {
                episode = null;
                episodeEnd = null;
                fractionalEpisodeRejected = true;
            }
        }

        bool candidateCapture = new[] { "airDate", "volume", "disc", "part", "cour", "absolute" }
            .Any(name => !string.IsNullOrWhiteSpace(TryGroup(capt, name)));
        if (title is not null)
        {
            // 有效捕获在归一化后判断，纯分隔符捕获不能抢先终止更具体的规则。
            title = Normalize(SafeReplace(BuiltinRulesCatalog.Separator, title, " "));
            if (string.IsNullOrWhiteSpace(title)) title = null;
        }
        bool meaningful = !string.IsNullOrWhiteSpace(title) || year.HasValue || season.HasValue || episode.HasValue
            || !string.IsNullOrWhiteSpace(seasonTitle) || fractionalEpisodeRejected || candidateCapture
            || (capt.Length > 0 && rule.ForceType && (string.Equals(rule.DefaultType, "movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rule.DefaultType, "tv", StringComparison.OrdinalIgnoreCase)));
        if (!meaningful)
        {
            execution.Trace(new(key, "rejected", "NoEffectiveCapture", rule.Id, scope,
                matchedSource, matchedSegment, hash, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return null;
        }
        execution.Trace(new(key, "matched", "EffectiveCapture", rule.Id, scope,
            matchedSource, matchedSegment, hash, Stopwatch.GetElapsedTime(started).TotalMilliseconds));

        string mediaType;
        if (rule.ForceType && !string.IsNullOrEmpty(rule.DefaultType))
        {
            mediaType = rule.DefaultType.ToLowerInvariant();
        }
        else
        {
            mediaType = InferMediaType(season, episode, rule.DefaultType, parentFolderName, matchedInput, year);
        }

        title ??= CleanedStem(Path.GetFileNameWithoutExtension(fileName));
        bool special = HasMixedCjkLatin(title);

        double baseConf = ScoreConfidence(title, mediaType, season, episode, year);
        double finalConf = Math.Min(1.0, baseConf + Math.Max(0.0, rule.ConfidenceBonus));

        List<RuleFieldEvidence> evidence = [];
        if (year is int y) evidence.Add(new("year", y, "UserRule", TryGroup(capt, "year") ?? y.ToString()));
        if (season is int sn) evidence.Add(new("season", sn, "UserRule", TryGroup(capt, "season") ?? sn.ToString()));
        if (episode is int ep) evidence.Add(new("episode", ep, "UserRule", TryGroup(capt, "episode") ?? ep.ToString()));
        if (episodeEnd is int end) evidence.Add(new("episodeEnd", end, "UserRule", TryGroup(capt, "episodeEnd") ?? end.ToString()));
        return new RuleParseResult(title, year, mediaType, season, episode, episodeEnd, finalConf, special, rule.Id,
            SeasonTitle: seasonTitle, FieldEvidence: evidence,
            RejectedFields: fractionalEpisodeRejected ? ["episode", "episodeEnd"] : null, ForceType: rule.ForceType,
            NumberingEvidence: CollectUserNumberingCaptures(capt, matchedInput, matchedSource, matchedSegment, context, rule.Id));
    }

    /// <summary>无有效捕获的输入不阻止同一规则继续检查祖先层</summary>
    private static bool HasEffectiveCapture(Match match, string input, ParseRule rule)
    {
        string title = Normalize(SafeReplace(BuiltinRulesCatalog.Separator, TryGroup(match, "title") ?? "", " "));
        int? year = TryParseInt(TryGroup(match, "year"));
        return title.Length > 0 || year is int y && MediaYearEvidence.ContainsYear([input], y)
            || ParseCjkSeason(TryGroup(match, "season")).HasValue || TryParseInt(TryGroup(match, "episode")).HasValue
            || new[] { "seasonTitle", "airDate", "volume", "disc", "part", "cour", "absolute" }
                .Any(name => !string.IsNullOrWhiteSpace(TryGroup(match, name)))
            || match.Length > 0 && rule.ForceType && (string.Equals(rule.DefaultType, "movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rule.DefaultType, "tv", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>用户规则后仅用显式标记补空季集，保留捕获、强制类型和拒绝证据</summary>
    private static RuleParseResult SupplementUserRule(RuleParseResult result, FileParseContext context)
    {
        if (result.ForceType && result.MediaType == "movie") return result;
        List<RuleFieldEvidence> evidence = [.. result.FieldEvidence ?? []];
        List<string> conflicts = [];
        int? season = result.Season;
        int? episode = result.Episode;
        int? episodeEnd = result.EpisodeEnd;
        bool episodeRejected = result.RejectedFields?.Contains("episode") == true
            || HasExplicitFractionalEpisode(Path.GetFileNameWithoutExtension(context.FileName));
        if (episodeRejected)
        {
            // 原捕获仍保留在 FieldEvidence；生效字段不能用祖先整数替代文件小数集。
            episode = null;
            episodeEnd = null;
        }
        List<(string Text, string Source)> layers = [(Path.GetFileNameWithoutExtension(context.FileName), "FileName")];
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
            layers.Add((context.RelativeSegments[i], $"RelativeSegment:{i}"));

        foreach ((string text, string source) in layers)
        {
            foreach (Match seasonMatch in MatchExplicitSeasons(text))
            {
                int? foundSeason = ParseCjkSeason(seasonMatch.Groups["season"].Value);
                if (foundSeason is not int sn) continue;
                if (season is null)
                {
                    season = sn;
                    evidence.Add(new("season", sn, source, seasonMatch.Value));
                }
                else if (season != sn)
                    conflicts.Add($"season：已有 {season} 与 {source} 显式标记 {sn} 冲突");
            }
            if (episodeRejected) continue;
            (int? _, int? foundEpisode, int? foundEnd) = ExtractSeasonEpisode(text);
            if (source == "FileName") foundEpisode ??= ExtractNumericFileEpisode(text);
            // 父目录范围描述整包，不作为当前文件的单集或范围证据。
            if (source != "FileName" && foundEnd is not null) continue;
            if (foundEpisode is int ep)
            {
                if (episode is null)
                {
                    episode = ep;
                    episodeEnd = foundEnd;
                    evidence.Add(new("episode", ep, source, text.Length <= 128 ? text : text[..128]));
                    if (foundEnd is int end) evidence.Add(new("episodeEnd", end, source, text.Length <= 128 ? text : text[..128]));
                }
                else if (source == "FileName" && episode != ep)
                    conflicts.Add($"episode：已有 {episode} 与文件显式标记 {ep} 冲突");
            }
        }

        string title = result.Title;
        if (season is not null)
        {
            // 只清理季标记，不把内置标题猜测覆盖用户捕获。
            foreach (Regex pattern in new[] { BuiltinRulesCatalog.SeasonOrdinalLatin, BuiltinRulesCatalog.SeasonWordLatin,
                BuiltinRulesCatalog.SeasonOnlyLatin, BuiltinRulesCatalog.SeasonChinese })
                title = ReplaceOutsideTitleParentheses(pattern, title, " ");
            title = Normalize(title);
            if (string.IsNullOrWhiteSpace(title)) title = result.Title;
        }
        string mediaType = result.ForceType ? result.MediaType
            : season is not null || episode is not null ? "tv" : result.MediaType;
        return result with
        {
            Title = title, Season = season, Episode = episode, EpisodeEnd = episodeEnd, MediaType = mediaType,
            Confidence = Math.Max(result.Confidence, ScoreConfidence(title, mediaType, season, episode, result.Year)),
            HasSpecialChars = HasMixedCjkLatin(title), FieldEvidence = evidence,
            Conflicts = conflicts.Count > 0 ? conflicts.Distinct().ToArray() : null,
            RejectedFields = episodeRejected ? ["episode", "episodeEnd"] : result.RejectedFields,
        };
    }

    /// <summary>文件显式小数集阻止从祖先把整数集补回来</summary>
    private static bool HasExplicitFractionalEpisode(string source)
    {
        foreach (Regex pattern in new[] { BuiltinRulesCatalog.SeasonEpisodeLatin, BuiltinRulesCatalog.EpisodeOnly })
        {
            Match match = SafeMatch(pattern, source);
            if (!match.Success) continue;
            Group digits = match.Groups["episodeEnd"].Success ? match.Groups["episodeEnd"] : match.Groups["episode"];
            if (digits.Success && HasFractionalEpisodeTail(source, digits.Index + digits.Length)) return true;
        }
        return false;
    }

    /// <summary>仅识别明确季号语法，不把作品尾数字或罗马续集当作补缺依据</summary>
    private static IReadOnlyList<Match> MatchExplicitSeasons(string text)
    {
        List<Match> matches = [];
        foreach (Regex pattern in new[] { BuiltinRulesCatalog.SeasonEpisodeLatin, BuiltinRulesCatalog.SeasonChinese,
            BuiltinRulesCatalog.SeasonOnlyLatin, BuiltinRulesCatalog.SeasonOrdinalLatin, BuiltinRulesCatalog.SeasonWordLatin })
        {
            matches.AddRange(SafeMatches(pattern, text));
        }
        return matches.OrderBy(m => m.Index).ToArray();
    }

    /// <summary>按 ParseScope 枚举返回该规则的候选输入字符串序列</summary>
    /// <remarks>
    /// 单元素 scope（FileName / ParentFolder / FullPath / RelativePath）yield 单值；
    /// AllAncestors 内→外逐层 yield（fileName-without-ext 优先，然后直接父、祖父、最外层），
    /// 调用方按 yield 顺序逐个尝试，先命中先返回 —— 越靠内层置信度越高（隐式排序，不需要外层加权）。
    /// </remarks>
    private static IEnumerable<string> EnumerateInputs(ParseScope scope, FileParseContext context)
    {
        string fileName = context.FileName;
        string fileNameStem = Path.GetFileNameWithoutExtension(fileName);
        string? parentFolderName = context.DirectParentFolderName;

        switch (scope)
        {
            case ParseScope.ParentFolder:
                yield return parentFolderName ?? string.Empty;
                yield break;

            case ParseScope.FullPath:
                // 兼容旧值：直接父目录 + 文件名拼接（保持与旧实现一致语义）
                yield return Path.Combine(parentFolderName ?? string.Empty, fileName);
                yield break;

            case ParseScope.RelativePath:
                // 标准化为 / 分隔的整条相对路径（含文件名，便于跨平台规则书写）
                List<string> parts = new(context.RelativeSegments.Count + 1);
                parts.AddRange(context.RelativeSegments);
                parts.Add(fileName);
                yield return string.Join('/', parts);
                yield break;

            case ParseScope.AllAncestors:
                // 内→外：文件名 stem → 直接父 → 祖父 → ... → 最外层
                yield return fileNameStem;
                for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
                {
                    yield return context.RelativeSegments[i];
                }
                yield break;

            case ParseScope.FileName:
            default:
                yield return fileName;
                yield break;
        }
    }

    // ---------- 内置规则 ----------

    /// <summary>内置规则执行：按「内→外」遍历路径段，逐段补全 season/episode/episodeEnd/year/title</summary>
    /// <remarks>
    /// 候选层级（内→外）：
    ///   [0] = 文件名 stem（最优先；技术细节最完整）
    ///   [1] = 直接父目录
    ///   [2] = 祖父目录
    ///   [...] = 一直到监控根下的第一级
    ///
    /// 字段填充顺序（先内后外，缺失字段才用更外层补）：
    ///   season / episode / episodeEnd → 内层优先（实际文件命名最具体）
    ///   year → 内层优先（剧集文件常无年份，需从父目录补）
    ///   title → 选择「**剥噪声后**最有信息量」的层段：剥掉季/集/年份/噪声后，剩余非空且至少含一个 CJK 或拉丁字母词的最外层（PT 站父目录承载剧名 + 文件名只剩 SxxExx 时，这一策略保证从父目录拿到剧名）。
    /// </remarks>
    private static RuleParseResult ApplyBuiltinRules(FileParseContext context)
    {
        string fileName = context.FileName;
        string stem = Path.GetFileNameWithoutExtension(fileName);

        // 按「内→外」组装候选层级
        List<string> layers = new(context.RelativeSegments.Count + 1) { stem };
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
        {
            layers.Add(context.RelativeSegments[i]);
        }

        // 逐字段「内层优先」提取
        int? season = null;
        int? episode = null;
        int? episodeEnd = null;
        int? year = null;
        string? seasonTitle = null;
        List<RuleFieldEvidence> fieldEvidence = [];
        foreach (string layer in layers)
        {
            (int? s, int? e, int? eEnd) = ExtractSeasonEpisode(layer);
            // 数字前缀只是兜底，同文件明确 E 标记及小数集拒绝优先。
            if (layer == stem && e is null && !HasExplicitFractionalEpisode(stem)
                && !HasFractionalEpisodeTail(fileName, stem.Length))
            {
                e = ExtractNumericFileEpisode(stem);
                if (e is int numericEpisode && !int.TryParse(stem, out _))
                    fieldEvidence.Add(new("episode", numericEpisode, "FileName", stem.Length <= 128 ? stem : stem[..128]));
            }
            if (season is null && s is int literalSeason)
            {
                Match? explicitSeason = MatchExplicitSeasons(layer)
                    .FirstOrDefault(match => ParseCjkSeason(match.Groups["season"].Value) == literalSeason);
                if (explicitSeason is not null)
                    fieldEvidence.Add(new("season", literalSeason, layers.IndexOf(layer) == 0 ? "FileName"
                        : $"RelativeSegment:{context.RelativeSegments.Count - layers.IndexOf(layer)}", explicitSeason.Value));
            }
            season ??= s;
            // 集号与末集是同一层的原子证据，父目录全集范围不能扩展文件单集。
            if (episode is null && e is not null && (layer == stem || eEnd is null))
            {
                episode = e;
                episodeEnd = eEnd;
            }
            year ??= ExtractYear(layer);
            seasonTitle ??= ExtractSeasonTitle(layer);
            if (season is not null && episode is not null && year is not null) break;
        }

        // 兜底：文件名 stem 是纯 1-3 位数字（如 01 / 02 / 100），把它当 episode
        // 限制 1-3 位避免与 4 位年份（1900-2099）混淆；4 位数字若超出年份范围（如 0815、9999）也接受
        if (episode is null && stem.Length is >= 1 and <= 4 && int.TryParse(stem, out int numericStem))
        {
            bool isYearLike = stem.Length == 4 && numericStem is >= 1900 and <= 2099;
            // 小数集守护（与 SxxEyy / EP 形态同口径）：无扩展名文件「11.5」会被 GetFileNameWithoutExtension
            // 把「.5」当扩展名剥掉、stem 截成「11」，不得再当集号——丢弃转低置信走 AI / 人工审核。
            // 带扩展名的「11.5.mkv」stem 为「11.5」，int.TryParse 天然拒绝小数形态，不会走进本兜底。
            bool isFractionalTruncated = HasFractionalEpisodeTail(Path.GetFileName(fileName), stem.Length);
            if (!isYearLike && !isFractionalTruncated)
            {
                episode = numericStem;
            }
        }

        // 兜底：整串「压制代号-集号」形态（DACZLNF-09 / YTYHXBYL-30）——字母段是压制组 / 缩写代号，
        // 数字段作集号（1900-2099 的 4 位年份除外，与纯数字 stem 兜底同口径）；该层不参与标题竞选（见 SelectTitle）
        if (episode is null)
        {
            Match tagEp = SafeMatch(BuiltinRulesCatalog.ReleaseTagEpisode, stem);
            if (tagEp.Success && TryParseInt(tagEp.Groups["episode"].Value) is int tagEpNum)
            {
                bool tagEpYearLike = tagEp.Groups["episode"].Value.Length == 4 && tagEpNum is >= 1900 and <= 2099;
                if (!tagEpYearLike) episode = tagEpNum;
            }
        }

        // 标题：从「最有信息量」的层选；优先级 = 剥噪声后非空 && 含字母词 → 越外层（PT 站剧名常在父目录）越优先
        (string title, bool titleMeaningful) = SelectTitle(layers, fileName, season, episode, year);

        // 类型推断时把所有层拼起来作为上下文（让父目录的「电影」「Anime」等 hint 也参与判断）
        string allContext = string.Join(' ', layers);
        string? parentFolderName = context.DirectParentFolderName;
        string mediaType = InferMediaType(season, episode, defaultType: null, parentFolderName, allContext, year);
        bool special = HasMixedCjkLatin(title);
        double confidence = ScoreConfidence(title, mediaType, season, episode, year);
        // 标题是兜底回退的 stem 原文（各层均无有效标题内容）时压低置信度：残渣 / 代号标题直查 TMDB
        // 只会零候选或误命中，压到阈值以下让流程走「本地备选标题重搜 → AI 兜底」链
        if (!titleMeaningful) confidence = Math.Min(confidence, 0.50);

        return new RuleParseResult(title, year, mediaType, season, episode, episodeEnd, confidence, special, MatchedRuleId: null, SeasonTitle: seasonTitle, FieldEvidence: fieldEvidence.Count > 0 ? fieldEvidence : null);
    }

    /// <summary>从候选层级（内→外）中选出最适合做标题的一层</summary>
    /// <remarks>
    /// 策略：
    ///   1. 先尝试**文件名 stem**（layers[0]）：剥噪声后若仍含有效标题内容，直接采用（细节最完整）。
    ///      「压制代号-集号」整串形态（DACZLNF-09）的层直接跳过——字母段是代号不是标题。
    ///   2. 否则按内→外顺序找第一个「剥噪声后有标题信息量」的层。
    ///      这能正确处理 PT 站「父目录承载剧名 / 文件名只剩 SxxExx 或纯集号」的场景。
    ///   3. 全部失败 → 回退到 stem 原文并标记 Meaningful=false，由调用方压低置信度，
    ///      避免技术残渣标题（"2026 60fps WEB 1"）以高置信直查 TMDB 零候选后白走 AI。
    /// </remarks>
    private static (string Title, bool Meaningful) SelectTitle(IReadOnlyList<string> layers, string fileName, int? season, int? episode, int? year)
    {
        for (int i = 0; i < layers.Count; i++)
        {
            string raw = layers[i];
            // 「压制代号-集号」整串层：字母段是压制组 / 缩写代号，跳过竞选让更外层（父目录）接手
            if (IsReleaseTagEpisodeLayer(raw)) continue;
            string cleaned = i == 0 ? ExtractTitle(raw, season, episode, year)
                : ExtractFolderTitle(raw, fileName, season, episode, year);
            if (HasMeaningfulContent(cleaned) && !IsNonIdentityTitle(cleaned) && !IsGenericFolder(cleaned))
            {
                return (cleaned, true);
            }
        }
        // 兜底：第一层（stem）原文
        return (layers.Count > 0 ? layers[0] : string.Empty, false);
    }

    /// <summary>该层是否为「压制代号-集号」整串形态（数字段为 4 位年份的不算，如 Show-2020 是「标题-年份」）</summary>
    private static bool IsReleaseTagEpisodeLayer(string layer)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.ReleaseTagEpisode, layer);
        if (!m.Success) return false;
        string ep = m.Groups["episode"].Value;
        return !(ep.Length == 4 && TryParseInt(ep) is >= 1900 and <= 2099);
    }

    /// <summary>剥噪声后的字符串是否「有标题信息量」：剔除纯数字与单字符 token 后仍有含 ≥2 个字母/CJK 的 token</summary>
    /// <remarks>
    /// 旧实现只数全串字母总量，技术残渣（"2026 60fps WEB 1" 的 fps / WEB）也能凑够 2 个字母被误判有效，
    /// 把父目录里的真实剧名挤出标题竞选。技术词主体由 Noise 词表剥除，这里按 token 粒度做第二道防线：
    /// 纯数字 token（年份 / 集号残留）与单字符 token（分隔残渣）不算信息量。
    /// </remarks>
    private static bool HasMeaningfulContent(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || IsHashOnlyTitle(s)) return false;
        foreach (string token in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2) continue;
            int letterCount = 0;
            bool allDigits = true;
            foreach (char ch in token)
            {
                if (IsCjk(ch) || IsLatinLetter(ch)) letterCount++;
                if (!char.IsDigit(ch)) allDigits = false;
            }
            if (allDigits) continue;
            if (letterCount >= 2) return true;
        }
        return false;
    }

    /// <summary>从单段字符串提取 season/episode/episodeEnd（按 SxxExx → 中文集号 → EP/E → 方括号 → 中文季 顺序）</summary>
    private static (int? season, int? episode, int? episodeEnd) ExtractSeasonEpisode(string s)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.SeasonEpisodeLatin, s);
        if (m.Success)
        {
            // 小数集守护：SxxEyy 紧跟「.单数字 + 分隔符/结尾」（如 S01E11.5.Recap）是动漫回顾/特别篇惯例，
            // 截成整数集 E11 会与正片第 11 集同号高置信直通归档（Overwrite 策略下最坏覆盖正片）。
            // 此处丢弃集号仅保留季号 → 下游「tv 缺集号」评分守护压到 0.50，确保走 AI / 人工审核。
            if (HasFractionalEpisodeTail(s, m))
            {
                return (TryParseInt(m.Groups["season"].Value), null, null);
            }
            int? eEnd = TryParseInt(m.Groups["episodeEnd"].Value);
            int? ep = TryParseInt(m.Groups["episode"].Value);
            if (eEnd is int e && ep is int sp && e < sp) eEnd = null;
            return (TryParseInt(m.Groups["season"].Value), ep, eEnd);
        }

        int? season = null;
        Match sm = SafeMatch(BuiltinRulesCatalog.SeasonChinese, s);
        if (sm.Success) season = ParseCjkSeason(sm.Groups["season"].Value);

        // 季号兜底：独立 Sxx（如父目录 Born.with.Luck.S01.2026）
        if (season is null)
        {
            Match som = SafeMatch(BuiltinRulesCatalog.SeasonOnlyLatin, s);
            if (som.Success) season = TryParseInt(som.Groups["season"].Value);
        }

        // 季号兜底：英文全词「Season NN」（标准目录布局 Show (2020)/Season 02/07.mkv 的季目录层；
        // 本方法按层级逐段调用，文件名层带「Season N」时同样生效）
        if (season is null)
        {
            Match swm = SafeMatch(BuiltinRulesCatalog.SeasonWordLatin, s);
            if (swm.Success) season = TryParseInt(swm.Groups["season"].Value);
        }

        if (season is null)
        {
            Match ordinal = SafeMatch(BuiltinRulesCatalog.SeasonOrdinalLatin, s);
            if (ordinal.Success) season = TryParseInt(ordinal.Groups["season"].Value);
        }

        // 罗马数字可能属于作品名（如 Lupin III），只在命名证据层保留候选，不直接赋季。

        (int? ep2, int? eEnd2) = ExtractEpisodeWithEnd(s);
        return (season, ep2, eEnd2);
    }

    private static (int? episode, int? episodeEnd) ExtractEpisodeWithEnd(string s)
    {
        foreach (Regex re in new[]
        {
            BuiltinRulesCatalog.EpisodeChinese,
            BuiltinRulesCatalog.EpisodeOnly,
            BuiltinRulesCatalog.BracketEpisode,
        })
        {
            Match m = SafeMatch(re, s);
            if (m.Success)
            {
                // 小数集守护（与 SxxEyy 同口径）：集号数字紧跟「.单数字 + 分隔符/结尾」（如 EP11.5 回顾集）
                // 是半集惯例，截成整数集会与正片同号冲突（父目录补出季号时还会以 0.85 直通归档）；
                // 丢弃本层集号转低置信走 AI / 人工审核。注意从「最后一个数字捕获组末尾」起检测而非 Match
                // 末尾——EpisodeOnly 的尾部分隔符是消耗组（「EP11.5」命中文本为「EP11.」，小数点已被吃进 Match）。
                Group lastDigits = m.Groups["episodeEnd"].Success ? m.Groups["episodeEnd"] : m.Groups["episode"];
                if (HasFractionalEpisodeTail(s, lastDigits.Index + lastDigits.Length))
                {
                    return (null, null);
                }
                int? ep = TryParseInt(m.Groups["episode"].Value);
                int? eEnd = TryParseInt(m.Groups["episodeEnd"].Value);
                if (eEnd is int e && ep is int sp && e < sp) eEnd = null;
                return (ep, eEnd);
            }
        }
        return (null, null);
    }

    /// <summary>SxxExx 命中后检测集号是否带小数尾巴（Match 末尾即集号数字末尾的形态用）</summary>
    /// <remarks>SeasonEpisodeLatin 的 Match 以集号数字收尾，可直接以 Match 末尾作为检测起点。</remarks>
    private static bool HasFractionalEpisodeTail(string s, Match m) =>
        HasFractionalEpisodeTail(s, m.Index + m.Length);

    /// <summary>检测 pos（集号数字串末尾）起是否为小数集尾巴「.单数字」（如 E11.5 / EP11.5 回顾/特别篇形态）</summary>
    /// <remarks>
    /// 形态要求「.」+ 恰好 1 位数字 + 分隔符或结尾，三道约束排除技术 token 误判：
    ///   · .1080p / .2008（多位数字）→ 不是小数尾巴；
    ///   · .4K / .5v2（数字后跟字母）→ 不是小数尾巴；
    ///   · .5.Recap / 串尾 .5 → 命中。
    /// 命中后由调用方丢弃集号转低置信（AI / 人工审核），避免 E11.5 截成 E11 与正片冲突。
    /// </remarks>
    private static bool HasFractionalEpisodeTail(string s, int pos)
    {
        if (pos + 1 >= s.Length || s[pos] != '.') return false;
        if (!char.IsAsciiDigit(s[pos + 1])) return false;
        if (pos + 2 >= s.Length) return true; // 「.5」收尾
        char next = s[pos + 2];
        return next is '.' or '-' or '_' or ' ' or '\t';
    }

    /// <summary>文件开头的纯集号可后接明确分辨率；不把任意数字前缀或小数集当作单集</summary>
    private static int? ExtractNumericFileEpisode(string stem)
    {
        Match numeric = SafeMatch(RuleRegexExecution.Get(@"^(?<episode>[0-9]{1,3})(?:$|[. _-]+(?:480|720|1080|1440|2160)[pP](?![A-Za-z0-9]))"), stem);
        return numeric.Success ? TryParseInt(numeric.Groups["episode"].Value) : null;
    }

    private static int? ExtractYear(string s)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.Year, MediaYearEvidence.WithoutTechnicalOrDateNumbers(s));
        return m.Success ? TryParseInt(m.Groups["year"].Value) : null;
    }

    private static string ExtractTitle(string stem, int? season, int? episode, int? year)
    {
        string s = CleanedStem(stem);

        // 年份 / 季集是常见的「标题边界」：取其前的部分作为标题，丢弃后续 release group 等噪声。
        // 边界在串首（boundary=0，如「S01E01.2026.…」无标题打头的命名）→ 标题为空，
        // 交由 SelectTitle 回落到更外层（父目录常承载剧名），而不是把边界后的技术残渣当标题
        int boundary = FindEarliestBoundary(s, year, season);
        if (boundary >= 0)
        {
            s = s[..boundary];
        }
        else
        {
            // 没有边界时，仍剥季集字段
            s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.SeasonEpisodeLatin, s, string.Empty);
            s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.SeasonChinese, s, string.Empty);
            s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.SeasonOnlyLatin, s, string.Empty);
            s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.EpisodeChinese, s, string.Empty);
            s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.EpisodeOnly, s, string.Empty);
            s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.BracketEpisode, s, string.Empty);
        }

        return Normalize(s);
    }

    /// <summary>找出年份 / 季集中最早出现的位置，作为标题边界（之后内容视为噪声）</summary>
    private static int FindEarliestBoundary(string s, int? year, int? season)
    {
        int best = -1;
        void Consider(Match m)
        {
            if (m.Success && m.Index >= 0 && (best == -1 || m.Index < best))
            {
                best = m.Index;
            }
        }

        if (year is not null) Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.Year, s));
        Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.SeasonEpisodeLatin, s));
        Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.SeasonChinese, s));
        Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.SeasonOnlyLatin, s));
        Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.EpisodeChinese, s));
        Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.EpisodeOnly, s));
        Consider(FindUnprotectedTitleBoundary(BuiltinRulesCatalog.BracketEpisode, s));
        // 新显式语法与字段提取使用同一模式；全角折叠保持索引长度不变，未知括号仍受保护。
        string folded = FoldStructuredWidth(s);
        Consider(FindUnprotectedTitleBoundary(StructuredCombined, folded));
        Consider(FindUnprotectedTitleBoundary(StructuredNx, folded));
        Consider(FindStructuredEpisodeBoundary(folded, season is not null));
        foreach (Match chinese in SafeMatches(StructuredCjk, folded))
            if (chinese.Groups["unit"].Value != "期" && !IntersectsTitleParentheses(folded, chinese)) Consider(chinese);

        return best;
    }

    private static string CleanedStem(string stem)
    { return CleanTitleNoise(stem); }

    private static string Normalize(string s)
    {
        s = s.Trim();
        // 折叠多空格
        StringBuilder sb = new(s.Length);
        bool prevSpace = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static string InferMediaType(int? season, int? episode, string? defaultType, string? parentFolderName, string anyContext, int? year = null)
    {
        // 明确季集 → tv
        if (season is not null || episode is not null) return "tv";

        if (!string.IsNullOrEmpty(defaultType))
        {
            string d = defaultType.ToLowerInvariant();
            if (d is "movie" or "tv") return d;
        }

        string ctx = ((parentFolderName ?? string.Empty) + " " + (anyContext ?? string.Empty)).ToLowerInvariant();
        if (ContainsAny(ctx, "tv", "剧集", "anime", "动漫", "season", "series") &&
            !ContainsAny(ctx, "movie", "电影"))
        {
            return "tv";
        }
        if (ContainsAny(ctx, "movie", "电影", "film"))
        {
            return "movie";
        }
        // 有年份但无任何剧集标记 → 视为电影（最常见命名模式）
        if (year is not null) return "movie";
        return "unknown";
    }

    private static bool ContainsAny(string s, params string[] needles)
    {
        foreach (string n in needles)
        {
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------- 置信度 ----------

    /// <summary>按需求文档 §3.3.1 评分表打分</summary>
    private static double ScoreConfidence(string? title, string mediaType, int? season, int? episode, int? year)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        if (!hasTitle && season is null && episode is null && year is null && mediaType == "unknown")
        {
            return 0.10;
        }
        if (!hasTitle) return 0.10;

        bool hasType = mediaType is "movie" or "tv";
        bool hasSeason = season is not null;
        bool hasEpisode = episode is not null;
        bool hasYear = year is not null;

        if (hasType && hasSeason && hasEpisode && hasYear) return 0.95;
        if (hasType && hasSeason && hasEpisode) return 0.85;
        if (hasType && hasYear && mediaType == "movie") return 0.85;
        // 剧集缺集号 → 压到 0.50 触发 AI 兜底：集号无法靠 TMDB 反推，需要 AI 从路径上下文再挖
        if (mediaType == "tv" && !hasEpisode) return 0.50;
        // 剧集仅缺季号（集号在）→ 0.70 走 TMDB 直查：下游「单季自动补季」可自动定 S01，
        // 多季剧转人工审核选季——两者都优于为一个大概率是 1 的季号动用 AI（还可能猜错）
        if (mediaType == "tv" && !hasSeason) return 0.70;
        if (hasType) return 0.70;
        return 0.50;
    }

    // ---------- 特殊字符（CJK + Latin 混杂）----------

    /// <summary>标题既有 ≥ 3 个 CJK 字符 又有 ≥ 3 个拉丁字符 → 命名混杂，触发 AI 走</summary>
    internal static bool HasMixedCjkLatin(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int cjk = 0, latin = 0;
        foreach (char ch in s)
        {
            if (IsCjk(ch)) cjk++;
            else if (IsLatinLetter(ch)) latin++;
        }
        return cjk >= 3 && latin >= 3;
    }

    private static bool IsCjk(char ch) =>
        (ch is >= '一' and <= '鿿') ||   // CJK Unified
        (ch is >= '぀' and <= 'ゟ') ||   // Hiragana
        (ch is >= '゠' and <= 'ヿ') ||   // Katakana
        (ch is >= '가' and <= '힯');     // Hangul

    private static bool IsLatinLetter(char ch) =>
        (ch is >= 'a' and <= 'z') || (ch is >= 'A' and <= 'Z');

    // ---------- helpers ----------

    private static Match SafeMatch(Regex re, string s)
    {
        return RuleRegexExecution.Match(re, s);
    }

    private static string SafeReplace(Regex re, string s, string replacement)
    {
        return RuleRegexExecution.Replace(re, s, replacement);
    }

    private static IReadOnlyList<Match> SafeMatches(Regex re, string s) => RuleRegexExecution.Matches(re, s);
    private static bool SafeIsMatch(Regex re, string s) => SafeMatch(re, s).Success;

    private static string? TryGroup(Match m, string name)
    {
        Group g = m.Groups[name];
        return g.Success && g.Value.Length > 0 ? g.Value : null;
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, out int v) ? v : null;

    // ---------- 本地备选搜索标题 ----------

    /// <summary>为解析结果挂载本地备选搜索标题（主标题拆分段 + 其余路径层标题）</summary>
    /// <remarks>
    /// 供 ProcessFileService 在「主标题 TMDB 不中 / 混排 / 低置信」将触发 AI 兜底之前逐个重搜 TMDB，
    /// 命中即免走 AI（典型场景：英文主标题在 TMDB zh-CN 搜不到，但路径目录里带中文剧名）。
    /// 顺序即搜索尝试顺序：纯 CJK 段优先（TMDB 首选语言 zh-CN 命中率最高），
    /// 组内按发现顺序（主标题拆分段 → 路径层内→外）。归一化去重（含与主标题比对），上限 5 个。
    /// </remarks>
    private static RuleParseResult WithAlternativeTitles(RuleParseResult result, FileParseContext context)
    {
        List<string> alts = BuildAlternativeTitles(result, context);
        if (IsNonIdentityTitle(result.Title))
        {
            // 保留原始规则标题作证据，只降低身份把握；可信父层作为候选，不擅自替换为任意目录。
            alts = alts.Where(t => !IsNonIdentityTitle(t) && !IsGenericFolder(t)).ToList();
            return result with { AlternativeTitles = alts, Confidence = Math.Min(result.Confidence, 0.3), HasIdentityEvidence = false };
        }
        return alts.Count == 0 ? result : result with { AlternativeTitles = alts };
    }

    /// <summary>日期、纯技术或发行宣传词不构成作品身份</summary>
    private static bool IsHashOnlyTitle(string title) => SafeIsMatch(RuleRegexExecution.Get(
        @"^[\[\(]?([A-Fa-f0-9]{8}|[A-Fa-f0-9]{32}|[A-Fa-f0-9]{40}|[A-Fa-f0-9]{64})[\]\)]?$"), title.Trim());

    internal static bool IsNonIdentityTitle(string title)
    {
        if (IsHashOnlyTitle(title)) return true;
        string normalized = Normalize(SafeReplace(BuiltinRulesCatalog.Separator, title, " "));
        if (SafeIsMatch(RuleRegexExecution.Get(@"^(?:19|20)\d{2}\s*(?:0?[1-9]|1[0-2])\s*(?:0?[1-9]|[12]\d|3[01])$"), normalized)) return true;
        // 数字集号不能为纯技术残渣提供作品身份；只去掉有分隔符的短数字前缀，避免误伤正常标题。
        string withoutEpisodePrefix = SafeReplace(RuleRegexExecution.Get(@"^[0-9]{1,4}\s+"), normalized, "");
        if (SafeIsMatch(RuleRegexExecution.Get(@"^(?:(?:4K|8K|2160p|1080p|720p|480p|HDR|UHD|HD|高清|蓝光|国语|粤语|中字|字幕|无水印|修复版)|\s)+$"), withoutEpisodePrefix)) return true;
        return !title.Contains('[') && !title.Contains('【') && string.IsNullOrWhiteSpace(CleanedStem(title));
    }

    private static bool IsGenericFolder(string title) => Normalize(title).ToLowerInvariant() is
        "download" or "downloads" or "movie" or "movies" or "tv" or "videos" or "media" or
        "下载" or "电影" or "剧集" or "动漫" or "影视";

    private const int MaxAlternativeTitles = 5;

    private static List<string> BuildAlternativeTitles(RuleParseResult result, FileParseContext context)
    {
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || IsUnverifiedTitleFragment(candidate)) return;
            string normalized = NormalizeForDedup(candidate);
            if (normalized.Length < 2) return;
            if (!seen.Add(normalized)) return;
            ordered.Add(candidate.Trim());
        }

        // 主标题只入去重集不入结果——主流程已用它搜索过
        seen.Add(NormalizeForDedup(result.Title));

        // 1. 主标题的混排拆分段（CJK 段 / 粘连副标段 / 拉丁词组段）
        foreach (string seg in SplitMixedSegments(result.Title)) Add(seg);

        // 2. 各路径层（stem + 目录段内→外）独立提取标题，整段与拆分段都作候选
        string stem = Path.GetFileNameWithoutExtension(context.FileName);
        List<string> layers = new(context.RelativeSegments.Count + 1) { stem };
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--) layers.Add(context.RelativeSegments[i]);
        for (int i = 0; i < layers.Count; i++)
        {
            string layer = layers[i];
            // 「压制代号-集号」层无标题价值（与 SelectTitle 同口径）
            if (IsReleaseTagEpisodeLayer(layer)) continue;
            string cleaned = i == 0 || (result.ForceType && result.MediaType == "movie")
                ? ExtractTitle(layer, result.Season, result.Episode, result.Year)
                : ExtractFolderTitle(layer, context.FileName, result.Season, result.Episode, result.Year);
            if (!HasMeaningfulContent(cleaned) || IsNonIdentityTitle(cleaned) || IsGenericFolder(cleaned)) continue;
            Add(cleaned);
            foreach (string seg in SplitMixedSegments(cleaned)) Add(seg);
        }

        if (ordered.Count <= 1) return ordered;

        // 纯 CJK 段整体前移（OrderByDescending 稳定排序，组内保持发现顺序），再截断上限
        return ordered
            .OrderByDescending(IsAllCjkTitle)
            .Take(MaxAlternativeTitles)
            .ToList();
    }

    /// <summary>把混排标题拆成可独立搜索的子段：CJK 段 / 与 CJK 粘连的拉丁副标段 / 拉丁词组段</summary>
    /// <remarks>
    /// 只在空白处分离明确的语言切换，粘连副标和紧随标题的数字仍随原片名保留。
    /// 「机动战士高达SEEDFREEDOM Mobile Suit Gundam Seed Freedom」保留两个完整作品标题，
    /// 不生成过宽的「机动战士高达」或无身份的「SEEDFREEDOM」片段。
    /// 纯单语标题拆出的唯一段与原串相同，由调用方去重剔除。
    /// </remarks>
    internal static List<string> SplitMixedSegments(string title)
    { return SplitIdentityTitleSegments(title); }

    /// <summary>标题是否为纯 CJK 段（含 CJK 且不含拉丁字母；空格 / 数字点缀允许）</summary>
    private static bool IsAllCjkTitle(string s)
    {
        bool hasCjk = false;
        foreach (char ch in s)
        {
            if (IsCjk(ch))
            {
                hasCjk = true;
                continue;
            }
            if (IsLatinLetter(ch)) return false;
        }
        return hasCjk;
    }

    private static int CountLatinLetters(string s)
    {
        int n = 0;
        foreach (char ch in s)
        {
            if (IsLatinLetter(ch)) n++;
        }
        return n;
    }

    /// <summary>备选标题去重归一化：去空白 + 小写（与文件夹复用守门同口径）</summary>
    private static string NormalizeForDedup(string s)
    {
        StringBuilder sb = new(s.Length);
        foreach (char ch in s)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>季标记统一后处理：对最终标题补「篇章名」与「尾部罗马数字季号」</summary>
    /// <remarks>
    /// 内置规则路径已在解析阶段提取过的字段此处不重复（仅缺失才补），保证两条规则路径口径一致：
    /// 即便命中用户规则（其 title 可能残留「II」「XXX篇」），也能补出 season / seasonTitle 并清理标题，
    /// 避免「Sword Art Online II」「鬼灭之刃 锻刀村篇」整串送 TMDB 失准。
    /// 罗马尾缀只保留候选，不自动改写标题或季号，避免电影续作和系列固有名称被误拆。
    /// </remarks>
    private static RuleParseResult PostProcessSeasonMarkers(RuleParseResult r)
    { return PreserveSeasonArcCandidate(r); }

    /// <summary>罗马数字季号 II-X → int（不含单 I；正则已限定 II 及以上）；非法返回 null</summary>
    private static int? ParseRomanSeason(string roman) => roman.ToUpperInvariant() switch
    {
        "II" => 2,
        "III" => 3,
        "IV" => 4,
        "V" => 5,
        "VI" => 6,
        "VII" => 7,
        "VIII" => 8,
        "IX" => 9,
        "X" => 10,
        _ => null,
    };

    /// <summary>提取季的篇章标题（中文「XXX篇」，如「锻刀村篇」）；无则 null</summary>
    private static string? ExtractSeasonTitle(string s)
    { return ExtractUnverifiedArcTitle(s); }

    private const string CjkDigits = "零一二三四五六七八九";

    /// <summary>解析季号：阿拉伯数字直转；中文数字（一 ~ 九十九，含「十」「两」）转 int；无法解析返回 null</summary>
    private static int? ParseCjkSeason(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (int.TryParse(s, out int n)) return n;
        return ParseCjkNumber1To99(s);
    }

    /// <summary>中文数字（1-99）转 int：个位「一~九」/「十」/「十几」/「几十」/「几十几」；越界或非法返回 null</summary>
    private static int? ParseCjkNumber1To99(string s)
    {
        int shi = s.IndexOf('十');
        if (shi < 0)
        {
            if (s.Length != 1) return null;
            int d = CjkDigit(s[0]);
            return d >= 1 ? d : null;
        }
        int tens = shi == 0 ? 1 : CjkDigit(s[0]);
        if (tens < 1) return null;
        int units = shi == s.Length - 1 ? 0 : CjkDigit(s[^1]);
        if (units < 0) return null;
        int v = tens * 10 + units;
        return v is >= 1 and <= 99 ? v : null;
    }

    /// <summary>单个中文数字字符 → 值（零~九 = 0~9，「两」= 2）；非中文数字字符返回 -1</summary>
    private static int CjkDigit(char c) => c == '两' ? 2 : CjkDigits.IndexOf(c);
}
