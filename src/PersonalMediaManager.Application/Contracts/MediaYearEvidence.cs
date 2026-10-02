using System.Text.RegularExpressions;
using System.Globalization;

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
    public static bool ContainsYear(IEnumerable<string> sources, int year) => year is >= 1900 and <= 2099 &&
        sources.Any(source => HasYear(WithoutDimensions(source), year));

    private static bool HasYear(string source, int year)
    {
        if (Regex.IsMatch(source, $@"(?<![0-9A-Za-z]){year}(?![0-9A-Za-z])", RegexOptions.CultureInvariant, Timeout)) return true;
        // 综艺日期规则可从合法 YYYYMMDD 捕获年份；不能把任意八位编号当日期。
        return Regex.Matches(source, $@"(?<![0-9A-Za-z]){year}[0-9]{{4}}(?![0-9A-Za-z])",
            RegexOptions.CultureInvariant, Timeout).Any(match => DateOnly.TryParseExact(match.Value,
                "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }
}
