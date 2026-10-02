using System.Text.RegularExpressions;
using System.Text;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    private static readonly Regex ExplicitAliasSeparator = new(
        @"(?:\s+|[._-]+)(?:AKA|a[.\s]+k[.\s]+a\.?|又名|也叫|亦名)(?:\s+|[._-]+)", BaseOptions, RegexTimeout);
    private const string ContentDescriptorPattern =
        @"劇場版|剧场版|電影版|电影版|特別篇|特别篇|特別編|特别编|総集編|总集篇|總集篇|番外篇|OVA|OAD|SP|NCOP|NCED|PV|Trailer";
    private static readonly Regex ContentDescriptorTitle = new(@"^(?:" + ContentDescriptorPattern + @")$", BaseOptions, RegexTimeout);
    private static readonly Regex ContentDescriptorFragment = new(
        @"(?<![\p{L}\p{N}])(?:" + ContentDescriptorPattern + @")(?![\p{L}\p{N}])", BaseOptions, RegexTimeout);
    private static readonly Regex UnverifiedLeadingBracketTitle = new(
        @"^(?:\[[^\[\]]{1,60}\]|【[^【】]{1,60}】)\s+(?<title>[^\[\]【】]+)$", BaseOptions, RegexTimeout);
    private static readonly Regex ReleaseOnlyTitle = new(
        @"^(?:(?:4K|8K|2160p|1080p|720p|480p|HDR|UHD|FHD|HD|高清|蓝光|藍光|国语|國語|粤语|粵語|中字|字幕|无水印|無水印)|(?:中英|中日|简日|簡日|简繁英|簡繁英)(?:双字|雙字|字幕|双语|雙語)(?:内嵌|內嵌)?|官网|官網|\s)+$",
        BaseOptions, RegexTimeout);
    private static readonly Regex ReleaseTitleAnchor = new(
        @"(?:4K|8K|2160p|1080p|720p|480p|HDR|UHD|FHD|HD|高清|蓝光|藍光|国语|國語|粤语|粵語|中字|字幕|无水印|無水印)|(?:中英|中日|简日|簡日|简繁英|簡繁英)(?:双字|雙字|字幕|双语|雙語)(?:内嵌|內嵌)?",
        BaseOptions, RegexTimeout);

    /// <summary>在实际输入中定位完整别名，保留原字形和原始区间</summary>
    private static IReadOnlyList<RuleTitleVariant>? BuildTitleVariants(string title, FileParseContext context,
        List<RuleTitleCandidateDecision> decisions)
    {
        List<RuleTitleVariant> variants = [];
        List<(string Text, string Source, int? SegmentIndex)> sources = GetTitleSources(context);
        List<string> candidates = SplitMixedSegments(title);
        foreach (Match descriptor in SafeMatches(ContentDescriptorFragment, title))
            AddTitleDecision(decisions, descriptor.Value, "Rejected", "ContentDescriptor", sources);
        foreach (string fragment in SplitRawScriptSegments(title).Where(s => IsArcOnlyTitle(s) && !IsContentDescriptorTitle(s)))
            AddTitleDecision(decisions, fragment, "Rejected", "UnverifiedArc", sources);

        // 用户规则只捕获 AKA 前主名时，仍从同一原文保留另一完整候选；不能借此改写用户主名。
        foreach ((string Text, string Source, int? SegmentIndex) source in sources)
        {
            string text = source.Text;
            if (!SafeIsMatch(ExplicitAliasSeparator, text)) continue;
            string sourceTitle = ExtractTitle(source.Source == "FileName" ? Path.GetFileNameWithoutExtension(text) : text,
                null, null, ExtractYear(text));
            List<string> aliases = SplitExplicitAliases(sourceTitle);
            if (aliases.Count < 2 || !aliases.Any(a => NormalizeForDedup(a) == NormalizeForDedup(title))) continue;
            candidates.AddRange(aliases);
        }
        foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            // 变体仅记录原文；未覆盖脚本的可见文字可记 Other，不据此提升规则身份置信度。
            if ((!HasMeaningfulContent(candidate) && candidate.Count(char.IsLetter) < 2)
                || IsUnverifiedTitleFragment(candidate))
            {
                string reason = IsContentDescriptorTitle(candidate) ? "ContentDescriptor"
                    : IsArcOnlyTitle(candidate) ? "UnverifiedArc" : "UnverifiedReleaseFragment";
                AddTitleDecision(decisions, candidate, "Rejected", reason, sources);
                continue;
            }
            // 仅容许现有标题清理已经执行的分隔符归一；不按子串中的脚本变化切断粘连主名。
            Regex sourcePattern = TitleSourcePattern(candidate);
            bool found = false;
            foreach ((string text, string source, int? segmentIndex) in sources)
            {
                Match span = SafeMatch(sourcePattern, text);
                if (!span.Success) continue;
                bool cjk = candidate.Any(IsCjk);
                bool latin = candidate.Any(IsLatinLetter);
                string scriptHint = cjk && latin ? "Mixed" : cjk ? "Cjk" : latin ? "Latin" : "Other";
                // CHT/ENG 等发布标签描述音轨或字幕；不能证明标题语言，更不授权繁简转换。
                variants.Add(new(candidate, scriptHint, Language: null, source, segmentIndex,
                    span.Index, span.Length, span.Value, candidate == title ? null : title));
                decisions.Add(new(candidate, "Accepted", "SourceTitleSpan", source, segmentIndex,
                    span.Index, span.Length, span.Value));
                found = true;
                break;
            }
            if (!found) decisions.Add(new(candidate, "Rejected", "SourceSpanMissing"));
        }
        AddUnverifiedDecimalQueryVariant(title, variants, decisions, sources);
        return variants.Count == 0 ? null : variants;
    }

    /// <summary>未知首块外的完整小数题名只供原文核验，不提升主名或生成普通别名</summary>
    private static void AddUnverifiedDecimalQueryVariant(string title, List<RuleTitleVariant> variants,
        List<RuleTitleCandidateDecision> decisions, List<(string Text, string Source, int? SegmentIndex)> sources)
    {
        Match structure = SafeMatch(UnverifiedLeadingBracketTitle, title);
        if (!structure.Success) return;
        string candidate = structure.Groups["title"].Value.Trim();
        if (!HasMeaningfulContent(candidate) || IsUnverifiedTitleFragment(candidate)) return;
        Regex pattern = TitleSourcePattern(candidate);
        foreach ((string sourceText, string source, int? segmentIndex) in sources)
        {
            Match span = SafeMatch(pattern, sourceText);
            if (!span.Success) continue;
            bool cjk = candidate.Any(IsCjk);
            bool latin = candidate.Any(IsLatinLetter);
            string script = cjk && latin ? "Mixed" : cjk ? "Cjk" : latin ? "Latin" : "Other";
            RuleTitleVariant variant = new(candidate, script, null, source, segmentIndex,
                span.Index, span.Length, span.Value, title);
            if (RuleSourceQueryEvidence.DecimalTitleQueries([variant]).Count == 0) continue;
            if (!variants.Any(existing => existing.Title == candidate && existing.Token == span.Value)) variants.Add(variant);
            decisions.Add(new(candidate, "Candidate", "UnverifiedReleasePrefix", source, segmentIndex,
                span.Index, span.Length, span.Value));
            return;
        }
    }

    /// <summary>分隔明确别名，内容描述、篇章及未知圆括号不参与脚本拆名</summary>
    private static List<string> SplitIdentityTitleSegments(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        List<string> explicitAliases = SplitExplicitAliases(title);
        if (explicitAliases.Count > 1) return explicitAliases;
        if (title.IndexOfAny(['(', ')', '（', '）']) >= 0 || SafeIsMatch(EditionMarker, title)) return [title];
        List<string> segments = SplitRawScriptSegments(title);
        if (segments.Any(s => IsContentDescriptorTitle(s) || IsArcOnlyTitle(s) || ParseRomanSeason(s) is not null))
            return [title];
        return segments;
    }

    private static List<string> SplitExplicitAliases(string title)
    {
        IReadOnlyList<Match> separators = SafeMatches(ExplicitAliasSeparator, title);
        if (separators.Count == 0) return [title];
        List<string> aliases = [];
        int start = 0;
        foreach (Match separator in separators)
        {
            string alias = title[start..separator.Index].Trim();
            if (alias.Length == 0) return [title];
            aliases.Add(alias);
            start = separator.Index + separator.Length;
        }
        string last = title[start..].Trim();
        if (last.Length == 0) return [title];
        aliases.Add(last);
        return aliases;
    }

    private static List<string> SplitRawScriptSegments(string title)
    {
        List<string> segments = [];
        StringBuilder current = new();
        bool? currentCjk = null;
        foreach (string word in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool? script = word.Any(IsCjk) ? true : word.Any(IsLatinLetter) ? false : null;
            if (current.Length > 0 && script is not null && currentCjk is not null && script != currentCjk)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
            currentCjk = script ?? currentCjk;
        }
        if (current.Length > 0) segments.Add(current.ToString());
        return segments;
    }

    private static bool IsContentDescriptorTitle(string title) => SafeIsMatch(ContentDescriptorTitle, title.Trim());

    private static bool IsArcOnlyTitle(string title)
    {
        Match arc = SafeMatch(BuiltinRulesCatalog.SeasonArc, title.Trim());
        return arc.Success && arc.Index == 0 && arc.Length == title.Trim().Length;
    }

    private static List<(string Text, string Source, int? SegmentIndex)> GetTitleSources(FileParseContext context)
    {
        List<(string Text, string Source, int? SegmentIndex)> sources = [(context.FileName, "FileName", null)];
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
            sources.Add((context.RelativeSegments[i], "RelativeSegment", i));
        return sources;
    }

    private static Regex TitleSourcePattern(string candidate) => new(
        @"(?<![\p{L}\p{N}])" + string.Join(@"[\s._-]+", candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))
        + @"(?![\p{L}\p{N}])", RegexOptions.CultureInvariant, RegexTimeout);

    private static void AddTitleDecision(List<RuleTitleCandidateDecision> decisions, string candidate, string decision,
        string reason, List<(string Text, string Source, int? SegmentIndex)> sources)
    {
        Regex sourcePattern = TitleSourcePattern(candidate);
        foreach ((string text, string source, int? segmentIndex) in sources)
        {
            Match span = SafeMatch(sourcePattern, text);
            if (!span.Success) continue;
            decisions.Add(new(candidate, decision, reason, source, segmentIndex, span.Index, span.Length, span.Value));
            return;
        }
        decisions.Add(new(candidate, decision, reason));
    }

    /// <summary>原文区间真实不等于标题语义已成立；未解析的发行结构不生成作品别名</summary>
    private static bool IsUnverifiedTitleFragment(string candidate)
    {
        if (IsContentDescriptorTitle(candidate) || IsArcOnlyTitle(candidate)
            || IsNonIdentityTitle(candidate) || IsGenericFolder(candidate)) return true;
        // 方括号既可能是组名、集数和发布标签，也可能属于作品名。保留原始主标题，
        // 但在没有结构解析前，不把这些整块或跨块残片声明为独立译名。
        if (candidate.IndexOfAny(['[', ']', '【', '】']) >= 0) return true;
        string normalized = Normalize(candidate);
        if (normalized is "迅雷下载" or "夸克下载") return true;
        // 必须整段由已知发行词组成，且含技术/字幕锚点；普通标题中的「官网」不删。
        return SafeIsMatch(ReleaseOnlyTitle, normalized) && SafeIsMatch(ReleaseTitleAnchor, normalized);
    }
}
