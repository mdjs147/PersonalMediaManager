using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>年份证据必须来自原始命名中的独立年份，分辨率的宽高不构成年份</summary>
public static class MediaYearEvidence
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    /// <summary>剔除完整尺寸词，保留其他位置的真实年份</summary>
    public static string WithoutDimensions(string source) => Regex.Replace(source,
        @"(?<![0-9])[0-9]{3,5}\s*[x×]\s*[0-9]{3,5}(?![0-9])", " ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    /// <summary>用户规则提示也需要独立原文证据；锁定的目录数据库身份不走此检查</summary>
    public static bool ContainsYear(IEnumerable<string> sources, int year)
    {
        if (year is < 1900 or > 2099) return false;
        string[] values = sources.ToArray();
        bool hasDateDirectories = values.Select((source, index) => (source, index)).Any(pair =>
            pair.source == year.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && pair.index + 2 < values.Length && IsMonth(values[pair.index + 1]) && IsDay(values[pair.index + 2]));
        return values.Any(source => !(hasDateDirectories && source == year.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && HasYear(WithoutTechnicalOrDateNumbers(source), year));
    }

    private static bool IsMonth(string text) => text.Length <= 2 && int.TryParse(text, out int month) && month is >= 1 and <= 12;
    private static bool IsDay(string text) => text.Length <= 2 && int.TryParse(text, out int day) && day is >= 1 and <= 31;

    /// <summary>完整日期、尺寸和像素数量不能证明作品发行年份</summary>
    public static string WithoutTechnicalOrDateNumbers(string source)
    {
        string value = WithoutDimensions(source);
        value = Regex.Replace(value, @"(?<![0-9])(?:19|20)[0-9]{2}(?:[-._/](?:0?[1-9]|1[0-2])[-._/](?:0?[1-9]|[12][0-9]|3[01])|(?:0[1-9]|1[0-2])(?:0[1-9]|[12][0-9]|3[01]))(?![0-9])",
            " ", RegexOptions.CultureInvariant, Timeout);
        return Regex.Replace(value, @"(?i)(?<![0-9])[0-9]{3,5}[pi](?![A-Za-z0-9])", " ", RegexOptions.CultureInvariant, Timeout);
    }

    private static bool HasYear(string source, int year) => Regex.IsMatch(source,
        $@"(?<![0-9A-Za-z]){year}(?![0-9A-Za-z])", RegexOptions.CultureInvariant, Timeout);
}
