using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    private static readonly Regex TitleMetadataBlock = new(
        @"\[(?<text>[^\[\]\r\n]{1,200})\]|【(?<text>[^【】\r\n]{1,200})】|\((?<text>[^()\r\n]{1,200})\)|（(?<text>[^（）\r\n]{1,200})）",
        BaseOptions, RegexTimeout);
    private static readonly Regex ReleaseLabel = new(
        @"(?:[- ]Raws|Subs?|字幕组|字幕組)$|(?:^|[- ])(?:Release[- ]?)?Group$|(?:https?://|www\.)", BaseOptions, RegexTimeout);
    private static readonly Regex IsolatedYearBlock = new(@"^(?:19|20)\d{2}$", BaseOptions, RegexTimeout);
    private static readonly Regex TechnicalBlockNumber = new(@"^[0-9]{1,3}(?:[-~][0-9]{1,3})?$", BaseOptions, RegexTimeout);
    private static readonly Regex ReleaseProgressBlock = new(@"^更新(?:至|到)\s*(?:第)?[0-9]{1,4}[集话話]$", BaseOptions, RegexTimeout);
    private static readonly Regex TitleTailSeparators = new(@"^[\s._+\-]*$", BaseOptions, RegexTimeout);
    private static readonly Regex ReleaseMetadataDirectory = new(
        @"^(?:(?:正片|花絮|特典|特辑|特輯|合集|高清|蓝光|藍光|原盘|原盤|国语|國語|粤语|粵語|中字|双语|雙語|字幕|完结|完結|全集)|[\s._-])+$",
        BaseOptions, RegexTimeout);
    private static readonly Regex AdditionalTechnicalMetadata = new(
        @"\b(?:BD|MKV|MP4|AVI|MA|LC|ASS(?:x[0-9]+)?|SRT(?:x[0-9]+)?|Main(?:8|10|12)p?|[A-F0-9]{8})\b|(?:HD)?(?:官网|官網)?(?:简繁英|簡繁英|中英|中日|简日|簡日)(?:双字|雙字|字幕|双语|雙語)(?:内嵌|內嵌)?|(?:HD)?(?:官网|官網)?(?:国语|國語|粤语|粵語|中字|字幕|无水印|無水印)+",
        BaseOptions, RegexTimeout);
    private static readonly Regex TechnicalChannelTuple = new(
        @"\([1-9]\.[0-2](?:ch|channels?)(?:\s*[,/+ ]\s*[1-9]\.[0-2](?:ch|channels?))*\)", BaseOptions, RegexTimeout);

    /// <summary>只清理有技术锚点的尾部与已识别括号角色</summary>
    private static string CleanTitleNoise(string stem)
    {
        string s = CleanTitleMetadataBlocks(stem);
        // 总量和明确季号是独立语法，不依赖会误伤作品词的通用词表。
        s = ReplaceOutsideUnknownTitleBlocks(BuiltinRulesCatalog.TotalCountNoise, s, " ");
        s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.SeasonOrdinalLatin, s, " ");
        s = ReplaceOutsideTitleParentheses(BuiltinRulesCatalog.SeasonWordLatin, s, " ");

        // 完整未知括号先保留；括号中的 1080p 不能授权删掉真实副标题的后半段。
        IReadOnlyList<Match> blocks = SafeMatches(TitleMetadataBlock, s);
        foreach (Match anchor in SafeMatches(BuiltinRulesCatalog.TechnicalAnchor, s.Replace('_', ' ')))
        {
            if (IsInsideTitleParentheses(s, anchor.Index)
                || blocks.Any(block => anchor.Index >= block.Index && anchor.Index < block.Index + block.Length))
                continue;
            string tail = s[anchor.Index..];
            if (!TryCleanTechnicalRegion(tail, allowReleaseSuffix: true, out string editionRemainder)) continue;
            s = s[..anchor.Index] + " " + editionRemainder;
            break;
        }
        return Normalize(SafeReplace(BuiltinRulesCatalog.Separator, s, " "));
    }

    /// <summary>整层发行分类目录不参与身份候选，不影响同词文件标题</summary>
    private static bool IsReleaseMetadataDirectory(string folder) => SafeIsMatch(ReleaseMetadataDirectory, folder)
        || IsContentDescriptorTitle(folder);

    /// <summary>多个未解析方括号不能凭原文残留升级身份置信度</summary>
    private static bool IsUnresolvedBracketTitle(string title)
    {
        IReadOnlyList<Match> blocks = SafeMatches(TitleMetadataBlock, title);
        return blocks.Count(block => block.Value[0] is '[' or '【') > 1
            && string.IsNullOrWhiteSpace(SafeReplace(TitleMetadataBlock, title, " "));
    }

    /// <summary>跳过未知圆括号内的编号，继续寻找括号外真实边界</summary>
    private static Match FindUnprotectedTitleBoundary(Regex pattern, string title)
    {
        foreach (Match match in SafeMatches(pattern, title))
            if (!IntersectsTitleParentheses(title, match)) return match;
        return Match.Empty;
    }

    private static bool IsInsideTitleParentheses(string title, int position)
    {
        int depth = 0;
        for (int i = 0; i < position; i++)
        {
            if (title[i] is '(' or '（') depth++;
            else if (title[i] is ')' or '）') depth = Math.Max(0, depth - 1);
        }
        return depth > 0;
    }

    private static string ReplaceOutsideTitleParentheses(Regex pattern, string title, string replacement)
    {
        string result = title;
        foreach (Match match in SafeMatches(pattern, title).Reverse())
            if (!IntersectsTitleParentheses(title, match))
                result = result.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        return result;
    }

    private static bool IntersectsTitleParentheses(string title, Match match) =>
        IsInsideTitleParentheses(title, match.Index) || title.AsSpan(match.Index, match.Length).IndexOfAny('(', '（') >= 0;

    /// <summary>总量只清已解包元数据，不在未知块里删出残缺作品名</summary>
    private static string ReplaceOutsideUnknownTitleBlocks(Regex pattern, string title, string replacement)
    {
        string result = title;
        foreach (Match match in SafeMatches(pattern, title).Reverse())
        {
            int depth = 0;
            for (int index = 0; index < match.Index; index++)
            {
                if (title[index] is '[' or '【') depth++;
                else if (title[index] is ']' or '】') depth = Math.Max(0, depth - 1);
            }
            if (depth > 0 || IntersectsTitleParentheses(title, match)
                || match.Value.IndexOfAny(['[', '【']) >= 0) continue;
            result = result.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        }
        return result;
    }

    private static bool IsCompleteStructuralTitleBlock(string content)
    {
        foreach (Regex pattern in new[] { BuiltinRulesCatalog.SeasonEpisodeLatin, BuiltinRulesCatalog.SeasonChinese,
            BuiltinRulesCatalog.SeasonOnlyLatin, BuiltinRulesCatalog.SeasonWordLatin,
            BuiltinRulesCatalog.EpisodeChinese, BuiltinRulesCatalog.EpisodeOnly, BuiltinRulesCatalog.TotalCountNoise })
        {
            Match match = SafeMatch(pattern, content);
            if (match.Success && match.Index == 0 && match.Length == content.Length) return true;
        }
        return false;
    }

    /// <summary>逐块核对技术、发布组与结构标记，未知副标题原样保留</summary>
    private static string CleanTitleMetadataBlocks(string source)
    {
        IReadOnlyList<Match> blocks = SafeMatches(TitleMetadataBlock, source);
        bool hasTechnicalAnchor = SafeIsMatch(BuiltinRulesCatalog.TechnicalAnchor, source.Replace('_', ' '));
        string s = source;
        foreach (Match block in blocks.Reverse())
        {
            if (IsInsideTitleParentheses(source, block.Index)) continue;
            string content = block.Groups["text"].Value;
            bool square = block.Value[0] is '[' or '【';
            bool anchoredBlock = SafeIsMatch(BuiltinRulesCatalog.TechnicalAnchor, content.Replace('_', ' '));
            string? replacement = null;
            if (SafeIsMatch(ReleaseProgressBlock, content))
                replacement = " ";
            else if (SafeIsMatch(IsolatedYearBlock, content) || IsCompleteStructuralTitleBlock(content))
                replacement = " " + content + " ";
            else if ((anchoredBlock || square && hasTechnicalAnchor || IsHashOnlyTitle(content))
                && TryCleanTechnicalRegion(content, allowReleaseSuffix: false, out string editions))
                replacement = " " + editions + " ";
            else if (square && SafeIsMatch(TechnicalBlockNumber, content))
                replacement = " ";
            else if (square && SafeIsMatch(ReleaseLabel, content))
                replacement = " ";
            if (replacement is not null) s = s.Remove(block.Index, block.Length).Insert(block.Index, replacement);
        }
        return s;
    }

    /// <summary>技术区域必须全段可解释，版名只能保留为原文</summary>
    private static bool TryCleanTechnicalRegion(string region, bool allowReleaseSuffix, out string editionRemainder)
    {
        Match[] editions = SafeMatches(EditionMarker, region).ToArray();
        editionRemainder = string.Join(' ', editions.Select(m => m.Value));
        string residual = region.Replace('_', ' ');
        // 先移除已核验版名后再核对技术词；版名不因出现在技术尾而消失。
        foreach (Match edition in editions.Reverse()) residual = residual.Remove(edition.Index, edition.Length).Insert(edition.Index, " ");
        // 嵌套声道参数只在已有技术锚点的区域内验证，未知圆括号仍保留。
        if (SafeIsMatch(BuiltinRulesCatalog.TechnicalAnchor, residual))
            residual = SafeReplace(TechnicalChannelTuple, residual, " ");
        if (allowReleaseSuffix)
        {
            Match suffix = SafeMatch(BuiltinRulesCatalog.ReleaseGroupSuffix, residual);
            // 单个分辨率加任意连字符单词仍可能是作品正文，至少两个技术锚点才解释未知发布组。
            if (suffix.Success && SafeMatches(BuiltinRulesCatalog.TechnicalAnchor, residual[..suffix.Index]).Count >= 2)
                residual = residual[..suffix.Index];
        }
        residual = MediaYearEvidence.WithoutDimensions(residual);
        residual = SafeReplace(BuiltinRulesCatalog.Noise, residual, " ");
        residual = SafeReplace(AdditionalTechnicalMetadata, residual, " ");
        return SafeIsMatch(TitleTailSeparators, residual);
    }
}
