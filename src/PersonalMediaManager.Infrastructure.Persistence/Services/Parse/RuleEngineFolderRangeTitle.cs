using System.Text.RegularExpressions;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    // 在原始目录段上识别，不能等分隔符折叠后把标题尾部任意两个数字当作范围。
    private static readonly Regex FolderTitleRange = new(
        @"^(?<title>.+?)(?<range>(?<![0-9])(?<start>[0-9]{1,3})[ \t]*[-~–][ \t]*(?<end>[0-9]{1,3}))(?=$|[.\s_\[【])(?<tail>.*)$",
        BaseOptions, RegexTimeout);

    /// <summary>独立文件单集佐证目录范围时，仅清理目录标题边界</summary>
    private static string ExtractFolderTitle(string folder, string fileName, int? season, int? episode, int? year)
    {
        if (IsReleaseMetadataDirectory(folder)) return string.Empty;
        // 已有显式季集语法自行界定标题，不能把 S01E30-40 的 E30 重新切成裸范围。
        if (ExtractSeasonEpisode(folder).episode is not null) return ExtractTitle(folder, season, episode, year);
        string source = folder;
        Match range = SafeMatch(FolderTitleRange, folder);
        if (range.Success && episode is int currentEpisode
            && TryParseInt(range.Groups["start"].Value) is int start
            && TryParseInt(range.Groups["end"].Value) is int end
            && start > 0 && start < end && currentEpisode >= start && currentEpisode <= end
            && HasIndependentSingleFileEpisode(fileName, currentEpisode))
        {
            string prefix = range.Groups["title"].Value.TrimEnd(' ', '\t', '.', '_', '-', '~', '–');
            string title = ExtractTitle(prefix, season, episode, year);
            string tail = CleanedStem(range.Groups["tail"].Value);
            // 后缀只容许已知技术噪声；“21-22号公路”等作品正文不能被范围吞掉。
            if (HasMeaningfulContent(title) && !IsNonIdentityTitle(title) && !IsGenericFolder(title)
                && (tail.Length == 0 || IsNonIdentityTitle(tail)))
                source = prefix;
        }

        // 目录范围只界定标题，不向当前文件填写 episode / episodeEnd，也不推断季号。
        return ExtractTitle(source, season, episode, year);
    }

    /// <summary>仅用文件自身的单集语法佐证，拒绝父层集号、范围和小数集</summary>
    private static bool HasIndependentSingleFileEpisode(string fileName, int expectedEpisode)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (HasExplicitFractionalEpisode(stem) || HasFractionalEpisodeTail(fileName, stem.Length)) return false;
        // 非法反向范围可能被现有集号提取器降为单值，也不能成为新标题边界的单集佐证。
        foreach (Regex pattern in new[] { BuiltinRulesCatalog.SeasonEpisodeLatin, BuiltinRulesCatalog.EpisodeChinese,
            BuiltinRulesCatalog.EpisodeOnly, BuiltinRulesCatalog.BracketEpisode })
        {
            if (SafeMatch(pattern, stem).Groups["episodeEnd"].Success) return false;
        }
        (int? _, int? episode, int? end) = ExtractSeasonEpisode(stem);
        episode ??= ExtractNumericFileEpisode(stem);
        return end is null && episode == expectedEpisode;
    }
}
