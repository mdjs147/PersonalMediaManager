using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Theory]
    [InlineData("Show Ver. 1.23 Final")]
    [InlineData("Show 1.23-12 Anthology")]
    public async Task DecimalVersionOrCollectionDoesNotBecomePreciseWorkQuery(string title)
    {
        RuleParseResult result = await Parse(title + ".mkv", null);
        RuleSourceQueryEvidence.DecimalTitleQueries(result.NamingEvidence?.TitleVariants, result.RejectedFields)
            .Should().BeEmpty();
        RuleSourceQueryEvidence.RequiresReview(result, title).Should().BeFalse();
    }

    [Theory]
    [InlineData("2.22 A (New) Beginning")]
    [InlineData("1.11 An (Old) Journey")]
    [InlineData("3.33 The (Final) Chapter")]
    public async Task DecimalSourceQueryPreservesExactTitlePunctuationWithoutInventingFields(string suffix)
    {
        string original = "Example Story " + suffix;
        RuleParseResult result = await Parse("[SampleLabel] " + original + " [BD x264 720p DTS-HD(6.1ch,2.0ch)].mkv",
            "[SampleLabel] Example Story 1.11-3.33 [BD x264 720p DTS-HD(6.1ch,2.0ch)]");
        result.AlternativeTitles![0].Should().Be(original);
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Token == original);
        result.Title.Should().Be("[SampleLabel] " + original.Replace('.', ' '), "未知首方括号保留原文，不能仅凭技术尾认定发布组");
        result.AlternativeTitles.Should().NotContain(original.Replace('.', ' '), "去掉未知首块后的规范化片段不能绕过原文核验");
        result.NamingEvidence.TitleCandidateDecisions.Should().Contain(decision =>
            decision.Candidate == original.Replace('.', ' ') && decision.Decision == "Candidate"
            && decision.Reason == "UnverifiedReleasePrefix");
        result.Season.Should().BeNull(); result.Episode.Should().BeNull(); result.EpisodeEnd.Should().BeNull();
        result.Year.Should().BeNull(); result.MediaType.Should().Be("unknown");
        RuleSourceQueryEvidence.RequiresReview(result, original).Should().BeTrue();
    }
}
