using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    // 版本标记可影响集数编排，作为证据保留；只能另建检索候选，不能当普通噪声全局删除。
    private static readonly Regex EditionMarker = new(
        @"(?i)(?<![A-Za-z])(?:HD[ ._-]+Remaster(?:ed)?|Remaster(?:ed)?|Special[ ._-]+Edition|Director'?s[ ._-]+Cut|Extended[ ._-]+Edition)(?![A-Za-z])|(?:高清)?重[製制]版|修[復复]版",
        BaseOptions, RegexTimeout);
    private static readonly Regex DashEpisode = new(
        @"^(?:\[[^\]]+\]\s*)*(?<title>[^\[\]]+?)\s+-\s+(?<episode>[0-9]{1,3})(?:v[0-9]+)?\s*(?:\[[^\]]*\]\s*)*$",
        BaseOptions, RegexTimeout);
    private static readonly Regex NumberedTitle = new(@"^(?<title>.+?)\s+(?<season>[1-9][0-9]?)$", BaseOptions, RegexTimeout);
    private static readonly Regex ConfirmedNumberedSeries = new(@"^(?:GRAND BLUE\s+)?碧[藍蓝]之海$", BaseOptions, RegexTimeout);
    private static readonly Regex LicensedArcSeries = new(
        @"^(?<series>食戟之[灵靈]|食戟のソーマ)\s+(?<arc>[弐貳贰餐神豪][之ノの]皿)$", BaseOptions, RegexTimeout);
    private static readonly Regex ReleaseTitleStructure = new(
        @"^\[(?<group>[^\]]{1,60})\]\s*\[(?<title>[^\]]{2,160})\]", BaseOptions, RegexTimeout);
    private static readonly Regex VerifiedReleaseGroupShape = new(@"(?:[- ]Raws|Subs?|字幕组|字幕組)$", BaseOptions, RegexTimeout);

    /// <summary>补充可溯源命名线索，未知编号不升级为已知季</summary>
    private static RuleParseResult FinalizeNaming(RuleParseResult result, FileParseContext context)
    {
        string stem = Path.GetFileNameWithoutExtension(context.FileName);
        string originalTitle = result.Title;
        if (result.Year is int year && !PersonalMediaManager.Application.Contracts.MediaYearEvidence.ContainsYear(
                new[] { context.FileName }.Concat(context.RelativeSegments), year))
        {
            string type = result.ForceType ? result.MediaType : InferMediaType(result.Season, result.Episode,
                null, context.DirectParentFolderName, string.Join(' ', new[] { stem }.Concat(context.RelativeSegments)));
            result = result with { Year = null, MediaType = type,
                Confidence = Math.Min(result.Confidence, ScoreConfidence(result.Title, type, result.Season, result.Episode, null)) };
        }
        List<string> uncertainties = [];
        List<string> conflicts = [.. result.Conflicts ?? []];
        List<RuleFieldEvidence> evidence = [.. result.FieldEvidence ?? []];
        string[] editions = new[] { stem }.Concat(context.RelativeSegments)
            .SelectMany(s => SafeMatches(EditionMarker, s)).Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();

        // 只有已识别的发布组前缀 + 紧随其后的独立标题块才提取；任意未知括号不能成为已证实标题。
        string? bracketTitle = ExtractReleaseBracketTitle(stem);
        if (bracketTitle is not null && (result.MatchedRuleId is null || SafeIsMatch(EditionMarker, result.Title)
            || result.Title.StartsWith('[')))
            result = result with { Title = bracketTitle, HasIdentityEvidence = true };
        originalTitle = result.Title;
        // 用户规则可能在标题组内重复捕获已核实的 SxxEyy；只删除与生效字段完全一致的标记。
        Match capturedEpisode = FindUnprotectedTitleBoundary(BuiltinRulesCatalog.SeasonEpisodeLatin, result.Title);
        if (capturedEpisode.Success && TryParseInt(capturedEpisode.Groups["season"].Value) == result.Season
            && TryParseInt(capturedEpisode.Groups["episode"].Value) == result.Episode)
        {
            string title = Normalize(result.Title.Remove(capturedEpisode.Index, capturedEpisode.Length));
            if (HasMeaningfulContent(title)) result = result with { Title = title };
        }

        Match dash = SafeMatch(DashEpisode, stem);
        if (!dash.Success) dash = MatchDashEpisodeWithTechnicalTail(stem);
        int? seasonCandidate = null;
        string? mappingSource = null;
        if (dash.Success && !(result.ForceType && result.MediaType == "movie"))
        {
            int episode = int.Parse(dash.Groups["episode"].Value);
            if (result.RejectedFields?.Contains("episode") != true && result.Episode is null)
            {
                result = result with { Episode = episode, MediaType = "tv" };
                evidence.Add(new("episode", episode, "FileName", dash.Groups["episode"].Value));
            }
            // 标准的单集分隔符只切走集号；标题内的数字仍保留到季号获独立证实之后。
            string sourceTitle = Normalize(dash.Groups["title"].Value);
            if (result.MatchedRuleId is null) result = result with
                { Title = ExtractTitle(sourceTitle, result.Season, result.Episode, result.Year) };
            originalTitle = sourceTitle;
            Match number = SafeMatch(NumberedTitle, sourceTitle);
            bool partOfExplicitSeason = number.Success && MatchExplicitSeasons(sourceTitle).Any(marker =>
                marker.Groups["season"].Index == number.Groups["season"].Index
                && marker.Groups["season"].Length == number.Groups["season"].Length);
            if (number.Success && !partOfExplicitSeason)
            {
                seasonCandidate = int.Parse(number.Groups["season"].Value);
                string baseTitle = number.Groups["title"].Value;
                // 用户已核实的系列名称和第三季结构；官网 https://www.grandblue-anime.com/ 同时有 Season3。
                // 这里只证明季号释义，不声称 TMDB 正典集序已核验；其他数字仍保留候选。
                bool confirmedSeries = seasonCandidate == 3 && SafeIsMatch(ConfirmedNumberedSeries, baseTitle);
                bool explicitAgreement = result.Season == seasonCandidate && (result.FieldEvidence ?? [])
                    .Any(e => e.Field == "season" && e.Value == seasonCandidate);
                if (confirmedSeries || explicitAgreement)
                {
                    mappingSource = confirmedSeries ? "ConfirmedSeriesNumbering" : "ExplicitSeasonAgreement";
                    if (result.Season is not null && result.Season != seasonCandidate)
                        conflicts.Add($"season：显式季号 {result.Season} 与已核实系列编号 {seasonCandidate} 冲突");
                    else
                    {
                        result = result with { Season = seasonCandidate,
                            Title = ExtractTitle(baseTitle, seasonCandidate, result.Episode, result.Year) };
                        evidence.Add(new("season", seasonCandidate.Value, mappingSource, number.Value));
                    }
                }
                else uncertainties.Add("SeasonOrSequelNumber");
            }
        }

        // 分季副标题必须限定完整系列身份。数据来源为正版平台的实际季目录，不能将「餐」按字形当作三。
        // https://www.myvideo.net.tw/details/3/12690 （2026-10-02 核验：貳/餐/神/豪之皿分别为2/3/4/5季）
        Match plate = SafeMatch(LicensedArcSeries, result.Title);
        if (plate.Success && result.Episode is not null && !(result.ForceType && result.MediaType == "movie"))
        {
            int mapped = plate.Groups["arc"].Value[0] switch { '弐' or '貳' or '贰' => 2, '餐' => 3, '神' => 4, _ => 5 };
            mappingSource = "LicensedSeasonCatalogue";
            seasonCandidate = mapped;
            evidence.Add(new("season", mapped, mappingSource, plate.Value));
            if (result.Season is not null && result.Season != mapped)
                conflicts.Add($"season：显式季号 {result.Season} 与分季副标题 {plate.Groups["arc"].Value} 的目录季号 {mapped} 冲突");
            else result = result with { Season = mapped, SeasonTitle = plate.Groups["arc"].Value,
                Title = plate.Groups["series"].Value, MediaType = "tv" };
        }

        result = PostProcessSeasonMarkers(result);
        Match roman = SafeMatch(BuiltinRulesCatalog.SeasonRoman, result.Title);
        if (result.Season is null && result.Episode is not null && roman.Success)
        {
            seasonCandidate ??= ParseRomanSeason(roman.Groups["roman"].Value);
            uncertainties.Add("SeasonOrSequelNumber");
        }
        if (mappingSource is not null && conflicts.Count == 0)
            result = result with { Confidence = Math.Max(result.Confidence,
                ScoreConfidence(result.Title, result.MediaType, result.Season, result.Episode, result.Year)) };
        List<RuleTitleCandidateDecision> titleDecisions = [];
        IReadOnlyList<RuleTitleVariant>? titleVariants = BuildTitleVariants(result.Title, context, titleDecisions);
        // 只在两个完整原文片段之间择主名；粘连中外文仍随同一个 CJK 主名保留。
        if (titleVariants is { Count: 2 }
            && SplitMixedSegments(result.Title).Count > 1
            && titleVariants.Count(v => v.ScriptHint is "Cjk" or "Mixed") == 1
            && titleVariants.Count(v => v.ScriptHint == "Latin") == 1)
        {
            string primary = titleVariants.Single(v => v.ScriptHint is "Cjk" or "Mixed").Title;
            result = result with { Title = primary };
            titleVariants = titleVariants.Select(v => v with { AliasOf = v.Title == primary ? null : primary }).ToArray();
        }
        else if (SplitExplicitAliases(result.Title) is { Count: > 1 } explicitAliases
            && titleVariants is not null && titleVariants.Any(v => v.Title == explicitAliases[0]))
        {
            result = result with { Title = explicitAliases[0] };
            titleVariants = titleVariants.Select(v => v with { AliasOf = v.Title == result.Title ? null : result.Title }).ToArray();
        }
        List<string> alternatives = BuildAlternativeTitles(result, context);
        HashSet<string> reviewOnlyTitles = titleDecisions.Where(decision => decision.Reason == "UnverifiedReleasePrefix")
            .Select(decision => decision.Candidate).ToHashSet(StringComparer.OrdinalIgnoreCase);
        alternatives.RemoveAll(candidate => reviewOnlyTitles.Contains(candidate));
        // 原始标题里的小数点可能有作品语义；精确原文只新增待核验 query，不改主名或季集。
        alternatives.InsertRange(0, RuleSourceQueryEvidence.DecimalTitleQueries(titleVariants, result.RejectedFields));
        alternatives.AddRange((titleVariants ?? []).Where(v => v.Title != result.Title && !reviewOnlyTitles.Contains(v.Title)).Select(v => v.Title));
        (RuleSourceNumberCandidate? numberingCandidate, string? corroboratedTitle) = FindParentCorroboratedNumber(result, context);
        if (numberingCandidate is not null)
        {
            uncertainties.Add(numberingCandidate.Interpretation);
            if (corroboratedTitle is not null) alternatives.Add(corroboratedTitle);
        }
        // 原始版名优先保留；删除版本后缀只用于第二搜索候选，不能据此证明同版或重排季集。
        string withoutEdition = Normalize(ReplaceOutsideTitleParentheses(EditionMarker, result.Title, " "));
        if (withoutEdition != result.Title && HasMeaningfulContent(withoutEdition))
        {
            alternatives.Add(withoutEdition);
            AddTitleDecision(titleDecisions, withoutEdition, "Candidate", "EditionNeedsCatalogue", GetTitleSources(context));
        }
        if (mappingSource == "LicensedSeasonCatalogue" && originalTitle != result.Title && !originalTitle.StartsWith('['))
            alternatives.Insert(0, originalTitle);
        AddUnverifiedArcCandidate(result, context, mappingSource, alternatives, uncertainties, titleDecisions);
        if (editions.Length > 0) uncertainties.Add("EditionNeedsCatalogueVerification");
        if (editions.Any(e => e.StartsWith("Special", StringComparison.OrdinalIgnoreCase))
            && !result.ForceType && result.Season is null)
            result = result with { MediaType = "unknown", Confidence = Math.Min(result.Confidence, 0.5) };
        result = result with
        {
            FieldEvidence = evidence.Count > 0 ? evidence : result.FieldEvidence,
            Conflicts = conflicts.Count > 0 ? conflicts.Distinct().ToArray() : null,
            HasSpecialChars = HasMixedCjkLatin(result.Title),
            NamingEvidence = new(originalTitle, editions, seasonCandidate, mappingSource,
                uncertainties.Count > 0 ? uncertainties : null,
                titleVariants, numberingCandidate, titleDecisions.Distinct().ToArray()),
            AlternativeTitles = alternatives.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !string.Equals(t, result.Title, StringComparison.OrdinalIgnoreCase)).Take(MaxAlternativeTitles).ToArray(),
        };
        if (conflicts.Count > 0) result = result with { Confidence = Math.Min(result.Confidence, 0.49) };
        if (result.MatchedRuleId is null && IsUnresolvedBracketTitle(result.Title))
            result = result with { Confidence = Math.Min(result.Confidence, 0.5), HasIdentityEvidence = false };
        if (IsNonIdentityTitle(result.Title) || IsContentDescriptorTitle(result.Title))
            result = result with { Confidence = Math.Min(result.Confidence, 0.3), HasIdentityEvidence = false,
                AlternativeTitles = (result.AlternativeTitles ?? []).Where(t => !IsNonIdentityTitle(t)
                    && !IsGenericFolder(t) && !IsContentDescriptorTitle(t)).ToArray() };
        return result;
    }

    private static string? ExtractReleaseBracketTitle(string stem)
    {
        Match match = SafeMatch(ReleaseTitleStructure, stem);
        if (!match.Success || !SafeIsMatch(VerifiedReleaseGroupShape, match.Groups["group"].Value)) return null;
        string title = Normalize(match.Groups["title"].Value.Replace('_', ' '));
        return HasMeaningfulContent(title) && !IsNonIdentityTitle(title) && !IsGenericFolder(title) ? title : null;
    }

    /// <summary>篇章只补原文候选，不截主名、不推季号或剧集类型</summary>
    private static RuleParseResult PreserveSeasonArcCandidate(RuleParseResult result)
    {
        string? arc = result.SeasonTitle;
        if (arc is not null && IsContentDescriptorTitle(arc)) arc = null;
        arc ??= ExtractUnverifiedArcTitle(result.Title);
        return result with { SeasonTitle = arc };
    }

    private static string? ExtractUnverifiedArcTitle(string title)
    {
        Match arc = SafeMatch(BuiltinRulesCatalog.SeasonArc, title);
        string? candidate = arc.Success ? arc.Groups["seasonTitle"].Value : null;
        return candidate is not null && !IsContentDescriptorTitle(candidate) ? candidate : null;
    }

    private static void AddUnverifiedArcCandidate(RuleParseResult result, FileParseContext context, string? mappingSource,
        List<string> alternatives, List<string> uncertainties, List<RuleTitleCandidateDecision> decisions)
    {
        if (result.SeasonTitle is null || mappingSource is not null) return;
        uncertainties.Add("ArcNeedsCatalogueVerification");
        List<(string Text, string Source, int? SegmentIndex)> sources = GetTitleSources(context);
        AddTitleDecision(decisions, result.SeasonTitle, "Candidate", "UnverifiedArc", sources);
        Match arc = SafeMatch(BuiltinRulesCatalog.SeasonArc, result.Title);
        if (!arc.Success || arc.Index + arc.Length != result.Title.Length) return;
        string baseTitle = Normalize(result.Title[..arc.Index]);
        if (!HasMeaningfulContent(baseTitle) || IsUnverifiedTitleFragment(baseTitle)) return;
        alternatives.Add(baseTitle);
        AddTitleDecision(decisions, baseTitle, "Candidate", "ArcBaseTitleNeedsCatalogue", sources);
    }
}
