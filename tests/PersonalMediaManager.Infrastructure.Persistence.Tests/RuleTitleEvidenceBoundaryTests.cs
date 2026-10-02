using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Theory]
    [InlineData("HD官网中英双字")]
    [InlineData("HD官網中英雙字")]
    [InlineData("官网中英字幕")]
    public async Task TitleEvidence_ReleaseDescriptionDoesNotReplaceUnresolvedAbbreviation(string tail)
    {
        RuleParseResult result = await Parse($"abc.1080p.{tail}[最新电影www.example.test].mp4", "迅雷下载");
        result.Title.Should().Be("abc", "已有明确分辨率和完整技术尾证据，可以移除发布描述");
        (result.AlternativeTitles ?? []).Should().NotContain(tail).And.NotContain("迅雷下载");
        result.NamingEvidence!.TitleVariants.Should().OnlyContain(v => v.Title == "abc");
        result.Year.Should().BeNull();
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData("Up 飞屋环游记", "飞屋环游记", "Up")]
    [InlineData("IT 小丑回魂", "小丑回魂", "IT")]
    [InlineData("Pi 圆周率", "圆周率", "Pi")]
    public async Task TitleEvidence_ShortLatinNamesStillRemainFullAliases(string title, string primary, string alias)
    {
        RuleParseResult result = await Parse(title + ".mkv", null);
        result.Title.Should().Be(primary);
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == alias && v.Language == null);
    }

    [Theory]
    [InlineData("官网奇谈 Web Tales", "官网奇谈")]
    [InlineData("中英双字之谜 Subtitle Mystery", "中英双字之谜")]
    [InlineData("HD高清人生 High Definition Life", "HD高清人生")]
    [InlineData("迅雷下载之谜 Download Mystery", "迅雷下载之谜")]
    public async Task TitleEvidence_TechnicalOrFolderWordsInsideRealTitlesAreNotDeleted(string title, string primary)
    {
        RuleParseResult result = await Parse(title + ".mkv", null);
        result.Title.Should().Be(primary);
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == primary);
    }

    [Theory]
    [InlineData("28-4K.mkv", "迅雷下载")]
    [InlineData("S01E14_4K_60fps.mkv", "夸克下载")]
    public async Task TitleEvidence_GenericDownloadFolderDoesNotBecomeWorkVariant(string file, string parent)
    {
        RuleParseResult result = await Parse(file, parent);
        (result.NamingEvidence!.TitleVariants ?? []).Should().NotContain(v => v.Title == parent);
        (result.AlternativeTitles ?? []).Should().NotContain(parent);
    }

    [Fact]
    public async Task TitleEvidence_ReleaseGroupIsNotAnEnglishWorkAlias()
    {
        RuleParseResult result = await Parse("[XYZ] テスト作品の教室 2nd Season 第02话 [WEB-DL][AVC_AAC][720P][CHS](0A1B2C3D).mp4",
            "[XYZ] テスト作品の教室 2nd Season [WEB-DL][AVC_AAC][720P][CHS][MP4]");
        result.Title.Should().Contain("テスト作品の教室");
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == "テスト作品の教室")
            .And.NotContain(v => v.Title.Contains("XYZ"));
        result.AlternativeTitles.Should().NotContain("[XYZ]");
        result.Season.Should().Be(2);
        result.Episode.Should().Be(2);
    }

    [Fact]
    public async Task TitleEvidence_UnparsedBracketStructuresDoNotProduceCrossBracketNames()
    {
        RuleParseResult result = await Parse("[示例标签][Example Fantasy Story][4th - 05][总第71][WEB-DL][720P_AVC_AAC][合集版][简日双语内嵌].mp4",
            "[示例标签][示例幻想作品 第四季][远行篇合集][WEB-DL][720P_AVC_AAC][简日双语内嵌]");
        (result.NamingEvidence!.TitleVariants ?? []).Should().BeEmpty();
        (result.AlternativeTitles ?? []).Should().NotContain(v => v.Contains('[') || v.Contains(']'));
        result.Title.Should().Contain("Example Fantasy", "原始未解结构应保留，不伪造干净作品名");
    }

    [Fact]
    public async Task TitleEvidence_BracketsInExplicitRuleTitleRemainInRawPrimary()
    {
        SeedRule(new ParseRule { Name = "literal title", Enabled = true, Priority = 1,
            Scope = ParseScope.FileName, Pattern = @"^(?<title>\[TEST\])", DefaultType = "unknown" });
        RuleParseResult result = await Parse("[TEST].mkv", null);
        result.Title.Should().Be("[TEST]");
        (result.NamingEvidence!.TitleVariants ?? []).Should().BeEmpty();
    }
}
