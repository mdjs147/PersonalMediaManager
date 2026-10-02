using System.Text;
using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>原文标点只形成待核验检索变体</summary>
public static class RuleSourceQueryEvidence
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>保留完整标题中单个小数编号，不重解释季集或版本</summary>
    public static IReadOnlyList<string> DecimalTitleQueries(IReadOnlyList<RuleTitleVariant>? variants,
        IReadOnlyList<string>? rejectedFields = null)
    {
        if (rejectedFields?.Any(field => field is "episode" or "episodeEnd") == true) return [];
        List<string> result = [];
        foreach (RuleTitleVariant variant in variants ?? [])
        {
            string token = variant.Token.Trim();
            if (token.Length is < 5 or > 250 || token == variant.Title || token.IndexOfAny(['[', ']', '【', '】']) >= 0)
                continue;
            MatchCollection decimals = Regex.Matches(token, @"(?<![\p{L}\p{N}.])\d{1,3}\.\d{1,3}(?![\p{L}\p{N}.])",
                RegexOptions.CultureInvariant, Timeout);
            // 多个小数可能是合集范围；前后没有作品文字时也不升级成标题候选。
            if (decimals.Count != 1) continue;
            Match number = decimals[0];
            if (!token[..number.Index].Any(char.IsLetter) || !token[(number.Index + number.Length)..].Any(char.IsLetter)) continue;
            if (Regex.IsMatch(token,
                @"(?i)(?:\b(?:v|ver|version|rev|revision|episode|ep|e)|第|[-–—])\s*[.:：]?\s*\d+\.\d+|\d+\.\d+\s*(?:ch\b|channels?\b|声道|聲道|fps\b|bit\b|gb\b|mb\b)",
                RegexOptions.CultureInvariant, Timeout)) continue;
            // 小数与整数混合的区间也是合集/编号线索，不能当作单部作品原名。
            if (Regex.IsMatch(token, @"\d+(?:\.\d+)?\s*(?:[-–—~～]|\bto\b|至)\s*\d+(?:\.\d+)?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) continue;
            result.Add(token);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
    }

    /// <summary>原文待核验关系不能借标点或空白变体绕过，也不凭搜索分数自动绑定</summary>
    public static bool RequiresReview(RuleParseResult rule, string query) =>
        rule.NamingEvidence?.TitleCandidateDecisions?.Any(decision => decision.Decision == "Candidate"
            && MatchesPendingRelation(decision.Candidate, query)) == true
        || DecimalTitleQueries(rule.NamingEvidence?.TitleVariants, rule.RejectedFields)
            .Any(candidate => MatchesPendingRelation(candidate, query));

    // 只传播待审核约束，绝不作为同一作品的正向证据，也不改查询原文或查询去重键。
    private static bool MatchesPendingRelation(string candidate, string query)
    {
        if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)) return true;
        string candidateKey = PendingRelationKey(candidate);
        return candidateKey.Length > 0 && string.Equals(candidateKey, PendingRelationKey(query), StringComparison.OrdinalIgnoreCase);
    }

    private static string PendingRelationKey(string title)
    {
        StringBuilder key = new(title.Length);
        foreach (Rune rune in title.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            // 包括符号及数字标点的变化也不能清除已有待审状态。
            // 这里只形成否决约束；2.22/222、A+B/AB 的检索字符串仍彼此独立。
            if (Rune.IsLetterOrDigit(rune))
                key.Append(rune.ToString());
        }
        return key.ToString();
    }

    /// <summary>尚未映射的来源编号不能被搜索分数或模型补值升级成正典字段</summary>
    public static IReadOnlyList<string> UnresolvedNumberingFields(RuleParseResult rule)
    {
        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach (string rejected in rule.RejectedFields ?? [])
            if (rejected is "season" or "episode" or "episodeEnd") fields.Add(rejected);
        foreach (RuleNumberingEvidence item in rule.NumberingEvidence ?? [])
        {
            if (item.State == RuleEvidenceState.Accepted) continue;
            if (rule.Episode is null && item.Source == "FileName" && item.Kind is RuleNumberingKind.AirDate or RuleNumberingKind.ShortAirDate
                or RuleNumberingKind.Volume or RuleNumberingKind.Disc or RuleNumberingKind.Part
                or RuleNumberingKind.Cour or RuleNumberingKind.Absolute or RuleNumberingKind.Issue
                or RuleNumberingKind.FractionalEpisode or RuleNumberingKind.InclusiveRange or RuleNumberingKind.ExplicitList)
                fields.Add("episode");
            if (rule.Season is null && rule.MediaType != "movie" && item.Kind is RuleNumberingKind.Part or RuleNumberingKind.Cour)
                fields.Add("season");
        }
        if (rule.MediaType == "tv" && rule.NamingEvidence is { EditionTags.Count: > 0 })
            fields.Add("edition");
        if (rule.MediaType != "movie" && rule.Season is null
            && (!string.IsNullOrWhiteSpace(rule.SeasonTitle) || rule.NamingEvidence?.SeasonCandidate is not null))
            fields.Add("season");
        return fields.Order(StringComparer.Ordinal).ToArray();
    }
}
