using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Tests.Entities;

/// <summary>WatchIgnoreRule.Matches：扩展名结尾匹配（含多重后缀）+ 关键词子串 + 启用/空模式守护</summary>
public sealed class WatchIgnoreRuleMatchesTests
{
    private static WatchIgnoreRule Ext(string pattern, bool enabled = true) =>
        new() { Type = IgnoreRuleType.Extension, Pattern = pattern, Enabled = enabled };

    private static WatchIgnoreRule Kw(string pattern, bool enabled = true) =>
        new() { Type = IgnoreRuleType.Keyword, Pattern = pattern, Enabled = enabled };

    [Theory(DisplayName = "扩展名规则：文件名以模式结尾即命中（含多重后缀临时文件）")]
    [InlineData(".part", "盗梦空间.2010.mkv.part", true)]
    [InlineData(".xltd", "庆余年.S01E01.mp4.bt.xltd", true)]   // 多重后缀：末段 .xltd 命中
    [InlineData(".part", "盗梦空间.2010.mkv", false)]
    [InlineData(".crdownload", "movie.crdownload", true)]
    public void ExtensionRule_EndsWith(string pattern, string fileName, bool expected)
    {
        Ext(pattern).Matches(fileName).Should().Be(expected);
    }

    [Theory(DisplayName = "扩展名规则大小写不敏感（模式已小写归一，文件名大写也命中）")]
    [InlineData(".part", "MOVIE.PART", true)]
    [InlineData(".xltd", "FILE.MP4.BT.XLTD", true)]
    public void ExtensionRule_CaseInsensitive(string pattern, string fileName, bool expected)
    {
        Ext(pattern).Matches(fileName).Should().Be(expected);
    }

    [Theory(DisplayName = "关键词规则：文件名包含子串即命中")]
    [InlineData("sample", "Inception.2010.Sample.mkv", true)]
    [InlineData("trailer", "Inception.2010.mkv", false)]
    public void KeywordRule_Contains(string pattern, string fileName, bool expected)
    {
        Kw(pattern).Matches(fileName).Should().Be(expected);
    }

    [Fact(DisplayName = "未启用规则恒不命中")]
    public void DisabledRule_NeverMatches()
    {
        Ext(".part", enabled: false).Matches("movie.part").Should().BeFalse();
    }

    [Theory(DisplayName = "空文件名 / 空模式恒不命中")]
    [InlineData("", "movie.part")]
    [InlineData(".part", "")]
    public void EmptyInputs_NeverMatch(string pattern, string fileName)
    {
        Ext(pattern).Matches(fileName).Should().BeFalse();
    }
}
