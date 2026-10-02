using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    private static readonly Regex DashEpisodeWithTechnicalTail = new(
        @"^(?:\[[^\]]+\]\s*)*(?<title>[^\[\]]+?)\s+-\s+(?<episode>[0-9]{1,3})(?:v[0-9]+)?\s*(?<tail>(?:(?:\([^()]*\)|\[[^\[\]]*\])\s*)+)$",
        BaseOptions, RegexTimeout);
    private static readonly Regex TechnicalTailBlock = new(@"\((?<text>[^()]*)\)|\[(?<text>[^\[\]]*)\]", BaseOptions, RegexTimeout);
    private static readonly Regex TitleWithUnmarkedNumber = new(
        @"^(?:\[[^\]]+\]\s*)*(?<title>[^\[\]()]+?)\s+(?<number>[0-9]{1,3})(?<tail>(?:\s*(?:\([^()]*\)|\[[^\[\]]*\]))+)$",
        BaseOptions, RegexTimeout);

    /// <summary>父目录同名仅佐证搜索标题，裸尾数仍保留集号或续作歧义</summary>
    private static (RuleSourceNumberCandidate? Candidate, string? SearchTitle) FindParentCorroboratedNumber(
        RuleParseResult result, FileParseContext context)
    {
        if (result.MatchedRuleId is not null || result.Episode is not null || context.DirectParentFolderName is null)
            return (null, null);
        string stem = Path.GetFileNameWithoutExtension(context.FileName);
        Match match = SafeMatch(TitleWithUnmarkedNumber, stem);
        if (!match.Success || !IsTechnicalTail(match.Groups["tail"].Value)
            || TryParseInt(match.Groups["number"].Value) is not int number || number <= 0)
            return (null, null);
        string fileTitle = ExtractTitle(match.Groups["title"].Value, result.Season, null, result.Year);
        string parentTitle = ExtractTitle(context.DirectParentFolderName, result.Season, null, result.Year);
        if (!HasMeaningfulContent(parentTitle) || IsNonIdentityTitle(parentTitle) || IsGenericFolder(parentTitle)
            || NormalizeForDedup(fileTitle) != NormalizeForDedup(parentTitle)) return (null, null);
        Group digits = match.Groups["number"];
        return (new(number, "FileName", digits.Index, digits.Length, digits.Value, "EpisodeOrSequelNumber"), parentTitle);
    }

    /// <summary>单集分隔符后只接受可逐词验证的技术圆括号</summary>
    private static Match MatchDashEpisodeWithTechnicalTail(string stem)
    {
        Match match = SafeMatch(DashEpisodeWithTechnicalTail, stem);
        if (!match.Success || !match.Groups["tail"].Value.Contains('(')) return Match.Empty;
        return IsTechnicalTail(match.Groups["tail"].Value) ? match : Match.Empty;
    }

    /// <summary>技术尾块逐个验证，不把未知副标题或普通括号当噪声</summary>
    private static bool IsTechnicalTail(string tail)
    {
        IReadOnlyList<Match> blocks = SafeMatches(TechnicalTailBlock, tail);
        if (blocks.Count == 0 || !SafeIsMatch(BuiltinRulesCatalog.TechnicalAnchor, tail.Replace('_', ' '))) return false;
        foreach (Match block in blocks)
        {
            string content = block.Groups["text"].Value;
            if (string.IsNullOrWhiteSpace(content)
                || !TryCleanTechnicalRegion(content, allowReleaseSuffix: false, out _)) return false;
        }
        return SafeReplace(TechnicalTailBlock, tail, " ").Trim().Length == 0;
    }
}
