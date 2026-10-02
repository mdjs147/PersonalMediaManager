using System.Text.RegularExpressions;

namespace PersonalMediaManager.Domain.Aggregates.ParseTasks;

/// <summary>强分隔的附加短片标记；仅提示需要人工映射，不推断特别篇季号</summary>
public static partial class MediaExtraClip
{
    /// <summary>CM 仅允许独立括号标签，避免误伤普通标题中的 CM 单词</summary>
    public static bool HasMarker(params string?[] texts) => texts.Any(text => text is not null && Marker().IsMatch(text));

    [GeneratedRegex(@"(?:^|[\s\[(【（._-])(?:NCOP|NCED|TV[_. -]?SPOTS)(?=$|[\s\])】）._-])|[\[(【（]\s*CM(?:[_. -]*(?:EP?)?[0-9]{1,4})?\s*[\])】）]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Marker();
}
