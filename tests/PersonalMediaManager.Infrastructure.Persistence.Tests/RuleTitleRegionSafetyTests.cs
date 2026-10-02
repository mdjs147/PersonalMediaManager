using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Theory]
    [InlineData("Mad.Max.2015.1080p.mkv", "Mad Max")]
    [InlineData("Stan.2024.1080p.WEB-DL.mkv", "Stan")]
    [InlineData("The.Web.2024.1080p.mkv", "The Web")]
    [InlineData("Extended.2024.1080p.mkv", "Extended")]
    [InlineData("A.Proper.Man.2024.1080p.mkv", "A Proper Man")]
    [InlineData("The.Last.Season.2024.1080p.mkv", "The Last Season")]
    [InlineData("Episode.One.2024.1080p.mkv", "Episode One")]
    [InlineData("Mad.Max.mkv", "Mad Max")]
    [InlineData("The.Web.mkv", "The Web")]
    [InlineData("Stan.mkv", "Stan")]
    [InlineData("Extended.mkv", "Extended")]
    public async Task TitleRegion_OrdinaryWordsAreNotGlobalNoise(string file, string title)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be(title);
        result.HasIdentityEvidence.Should().BeTrue();
    }

    [Theory]
    [InlineData("Example.Title.1080p.NF.WEB-DL.H264.DDP5.1-Group.mkv")]
    [InlineData("Example.Title.2160p.UHD.BluRay.HEVC.HDR10.Atmos-Group.mkv")]
    [InlineData("Example.Title.1080p.BluRay.DTS-HD.MA.5.1.x264-Group.mkv")]
    [InlineData("Example.Title.1080p.WEB-DL.AAC2.0.H264-Group.mkv")]
    [InlineData("Example Title (1080p WEB-DL H264 AAC).mkv")]
    [InlineData("Example Title [1080P][AVC_AAC][CHS].mkv")]
    [InlineData("Example Title [BD 1920x1080 HEVC x265 10bit ASSx2].mkv")]
    [InlineData("Example Title [BD x264 1080p DTS-HD(6.1ch,2.0ch)].mkv")]
    public async Task TitleRegion_AnchoredTechnicalTailStillCleans(string file)
    {
        (await Parse(file, null)).Title.Should().Be("Example Title");
    }

    [Theory]
    [InlineData("Example (Flames of Destiny).2024.1080p.mkv", "Example (Flames of Destiny)")]
    [InlineData("Example (命运之焰).2024.1080p.mkv", "Example (命运之焰)")]
    [InlineData("Example (1080p Finale).mkv", "Example (1080p Finale)")]
    [InlineData("Example.1080p.(Max).mkv", "Example 1080p (Max)")]
    [InlineData("Example (1984 Tale).2024.1080p.mkv", "Example (1984 Tale)")]
    [InlineData("Example (S02E03 Returns).2024.1080p.mkv", "Example (S02E03 Returns)")]
    [InlineData("Example (Season 2 Finale).mkv", "Example (Season 2 Finale)")]
    [InlineData("Example (Summer 2024 Story).mkv", "Example (Summer 2024 Story)")]
    [InlineData("Example (全24集回忆).mkv", "Example (全24集回忆)")]
    [InlineData("The.Web-Story.mkv", "The Web Story")]
    [InlineData("The.1080p.Story.mkv", "The 1080p Story")]
    [InlineData("The.1080p-Story.mkv", "The 1080p Story")]
    public async Task TitleRegion_UnknownParenthesesAndInternalWordsRemainIdentity(string file, string title)
    {
        (await Parse(file, null)).Title.Should().Be(title);
    }

    [Theory]
    [InlineData("[Revenge] of the Nerds.1080p.mkv", "[Revenge] of the Nerds")]
    [InlineData("[Unknown] Example Title.1080p.H264.mkv", "[Unknown] Example Title")]
    [InlineData("【Unknown】 Example Title.1080p.H264.mkv", "【Unknown】 Example Title")]
    public async Task TitleRegion_UnknownLeadingBracketIsNotAReleaseGroup(string file, string title)
    {
        (await Parse(file, null)).Title.Should().Be(title);
    }

    [Theory]
    [InlineData("[Example-Raws] Example Title.1080p.H264.mkv")]
    [InlineData("[Example Subs] Example Title.1080p.H264.mkv")]
    [InlineData("【示例字幕组】 Example Title.1080p.H264.mkv")]
    [InlineData("[ReleaseGroup] Example Title.1080p.H264.mkv")]
    public async Task TitleRegion_ExplicitReleaseGroupLabelStillCleans(string file)
    {
        (await Parse(file, null)).Title.Should().Be("Example Title");
    }

    [Theory]
    [InlineData("示例作品 [更新至18集]")]
    [InlineData("示例作品【更新至第18集】")]
    [InlineData("示例作品 (更新到18話)")]
    public async Task TitleRegion_CompleteProgressMetadataCannotLeaveBrokenTitle(string folder)
    {
        RuleParseResult result = await Parse("S01E16.2026.1080p.mkv", folder);
        result.Title.Should().Be("示例作品");
        result.Season.Should().Be(1);
        result.Episode.Should().Be(16);
        result.EpisodeEnd.Should().BeNull();
    }

    [Theory]
    [InlineData("更新中的故事")]
    [InlineData("示例作品 [更新至18集的日子]")]
    [InlineData("示例作品【更新至未知未来】")]
    public async Task TitleRegion_UpdateWordsInUnknownTitleRemainWhole(string title)
    {
        RuleParseResult result = await Parse(title + ".2024.mkv", null);
        result.Title.Should().Be(title);
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData("Example Movie 劇場版.2024.1080p.mkv", "劇場版")]
    [InlineData("Example Show 特别篇 - 01.mkv", "特别篇")]
    [InlineData("Example Show 特別篇 - 01.mkv", "特別篇")]
    public async Task TitleRegion_ContentDescriptorCannotBecomeAliasOrPrimary(string file, string descriptor)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Contain("Example").And.Contain(descriptor);
        result.Season.Should().BeNull();
        (result.AlternativeTitles ?? []).Should().NotContain(descriptor);
        (result.NamingEvidence!.TitleVariants ?? []).Should().NotContain(v => v.Title == descriptor);
        result.NamingEvidence.TitleCandidateDecisions.Should().Contain(d =>
            d.Candidate == descriptor && d.Decision == "Rejected" && d.Reason == "ContentDescriptor");
    }

    [Fact]
    public async Task TitleRegion_StandaloneContentDescriptorDoesNotAssertWorkIdentity()
    {
        RuleParseResult result = await Parse("特别篇 - 01.mkv", null);
        result.Title.Should().Be("特别篇");
        result.HasIdentityEvidence.Should().BeFalse();
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData("AKA")]
    [InlineData("a.k.a.")]
    [InlineData("又名")]
    [InlineData("也叫")]
    public async Task TitleRegion_ExplicitAliasSeparatorProducesCompleteSourceCandidates(string separator)
    {
        string file = $"测试作品 {separator} Test Programme.S02E03.mkv";
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be("测试作品");
        result.AlternativeTitles.Should().Contain("Test Programme");
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == "测试作品")
            .And.Contain(v => v.Title == "Test Programme")
            .And.NotContain(v => v.Title.Contains("AKA", StringComparison.OrdinalIgnoreCase));
        foreach (RuleTitleVariant variant in result.NamingEvidence.TitleVariants!)
        {
            file.Substring(variant.Start, variant.Length).Should().Be(variant.Token);
            variant.Language.Should().BeNull();
        }
    }

    [Fact]
    public async Task TitleRegion_UserAliasCaptureStillKeepsOtherCompleteAlias()
    {
        SeedRule(new ParseRule { Name = "显式别名", Enabled = true, Priority = 1,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>Test Programme) AKA .+?\.S(?<season>\d+)E(?<episode>\d+)", DefaultType = "tv" });
        RuleParseResult result = await Parse("Test Programme AKA 测试作品.S02E03.mkv", null);
        result.Title.Should().Be("Test Programme", "用户明确捕获的主名保持优先");
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == "测试作品");
        result.AlternativeTitles.Should().Contain("测试作品");
    }

    [Theory]
    [InlineData("Example Movie 无限列车篇.2020.mkv", "Example Movie 无限列车篇")]
    [InlineData("示例作品 海岸篇 第01集.mkv", "示例作品 海岸篇")]
    public async Task TitleRegion_UnverifiedArcKeepsFullPrimaryAndDoesNotInventSeason(string file, string title)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be(title);
        result.Season.Should().BeNull();
        result.NamingEvidence!.Uncertainties.Should().Contain("ArcNeedsCatalogueVerification");
        result.NamingEvidence.TitleCandidateDecisions.Should().Contain(d => d.Reason == "UnverifiedArc");
    }

    [Theory]
    [InlineData("Example 3 - 01.mkv", "Example 3")]
    [InlineData("示例作品SEEDFREEDOM Example Seed Freedom.2024.mkv", "示例作品SEEDFREEDOM")]
    [InlineData("Example (特别篇).mkv", "Example (特别篇)")]
    public async Task TitleRegion_UnknownIdentitySuffixesRemainWhole(string file, string title)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be(title);
        result.Season.Should().BeNull();
        (result.AlternativeTitles ?? []).Should().NotContain("SEEDFREEDOM");
    }
}
