using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class RuleSourceQueryEvidenceTests
{
    [Theory]
    [InlineData("ArcBaseTitleNeedsCatalogue")]
    [InlineData("EditionNeedsCatalogue")]
    [InlineData("UnverifiedReleasePrefix")]
    public void UnverifiedTitleCandidateCannotBecomeAnAutomaticIdentity(string reason)
    {
        RuleParseResult rule = new("Example Full Title", null, "unknown", null, null, null, 0.8, false, null,
            AlternativeTitles: ["Example"], NamingEvidence: new("Example Full Title", [],
                TitleCandidateDecisions: [new("Example", "Candidate", reason)]));
        RuleSourceQueryEvidence.RequiresReview(rule, "Example").Should().BeTrue();
        RuleSourceQueryEvidence.RequiresReview(rule, "Example Full Title").Should().BeFalse();
    }

    [Fact]
    public void AcceptedCompleteAliasDoesNotAcquireCandidateRestriction()
    {
        RuleParseResult rule = new("测试作品", null, "unknown", null, null, null, 0.8, false, null,
            AlternativeTitles: ["Example"], NamingEvidence: new("测试作品", [],
                TitleCandidateDecisions: [new("Example", "Accepted", "SourceTitleSpan")]));
        RuleSourceQueryEvidence.RequiresReview(rule, "Example").Should().BeFalse();
        RuleSourceQueryEvidence.RequiresReview(rule, "Example!").Should().BeFalse();
    }

    [Theory]
    [InlineData("Example", "Example!")]
    [InlineData("Example Show", "Example  Show")]
    [InlineData("Example Show", "Example\tShow")]
    [InlineData("Example Show", "Example: Show")]
    [InlineData("例子", "《例子》")]
    [InlineData("Example", "Example+")]
    [InlineData("A+B", "AB")]
    [InlineData("Numbered Story 2.22", "Numbered Story 222")]
    [InlineData("Numbered Story 2.22", "Numbered Story 2 22")]
    [InlineData("Café", "Cafe\u0301")]
    public void PresentationVariantsKeepPendingRelation(string candidate, string query)
    {
        RuleParseResult rule = new("Full source title", null, "unknown", null, null, null, 0.8, false, null,
            AlternativeTitles: [candidate], NamingEvidence: new("Full source title", [],
                TitleCandidateDecisions: [new(candidate, "Candidate", "UnverifiedRelation")]));
        RuleSourceQueryEvidence.RequiresReview(rule, query).Should().BeTrue();
        rule.AlternativeTitles.Should().Equal([candidate], "待审判断不应改写原始搜索词");
    }

    [Theory]
    [InlineData("Example", "Example Other")]
    [InlineData("Numbered Story 2.22", "Numbered Story 3.33")]
    [InlineData("!", "?")]
    public void DistinctTitlesDoNotSharePendingRelation(string candidate, string query)
    {
        RuleParseResult rule = new("Full source title", null, "unknown", null, null, null, 0.8, false, null,
            NamingEvidence: new("Full source title", [],
                TitleCandidateDecisions: [new(candidate, "Candidate", "UnverifiedRelation")]));
        RuleSourceQueryEvidence.RequiresReview(rule, query).Should().BeFalse();
    }

    [Theory]
    [InlineData(RuleNumberingKind.AirDate)]
    [InlineData(RuleNumberingKind.ShortAirDate)]
    [InlineData(RuleNumberingKind.Volume)]
    [InlineData(RuleNumberingKind.Absolute)]
    [InlineData(RuleNumberingKind.Cour)]
    public void UnmappedNumberingKeepsMissingFieldsUnresolved(RuleNumberingKind kind)
    {
        RuleParseResult rule = new("Example", null, "tv", null, null, null, 0.99, false, null,
            NumberingEvidence: [new(kind, RuleEvidenceState.Candidate, "sourceNumber", "FileName", null, 0, 1, "2", 2)]);
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule).Should().Contain("episode");
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule with { Season = 1, Episode = 3 }).Should().BeEmpty();
    }

    [Fact]
    public void RejectedNumberingCannotBeLaunderedByLaterFilledFields()
    {
        RuleParseResult rule = new("Example", null, "tv", 1, 3, null, 0.99, false, null,
            RejectedFields: ["episode"]);
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule).Should().Contain("episode");
    }

    [Fact]
    public void TvEditionNeedsMappingEvenWhenSourceSeasonEpisodeAreComplete()
    {
        RuleParseResult rule = new("Example HD Remaster", null, "tv", 1, 3, null, 0.99, false, null,
            NamingEvidence: new("Example HD Remaster", ["HD Remaster"],
                Uncertainties: ["EditionNeedsCatalogueVerification"]));
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule).Should().Contain("edition");
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule with { MediaType = "movie", Season = null, Episode = null })
            .Should().BeEmpty("电影完整原题不需要季内集序映射，去版名查询仍走独立候选守门");
    }

    [Fact]
    public void MovieInParentPackDoesNotRequireASeasonEpisodeCoordinate()
    {
        RuleParseResult rule = new("Example Movie", 2024, "movie", null, null, null, 0.9, false, null,
            NumberingEvidence: [new(RuleNumberingKind.InclusiveRange, RuleEvidenceState.Candidate,
                "episode", "RelativeSegment", 0, 0, 7, "[01-10]", 1, 10)]);
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule).Should().BeEmpty();
        RuleSourceQueryEvidence.UnresolvedNumberingFields(rule with
        {
            NumberingEvidence = [new(RuleNumberingKind.Cour, RuleEvidenceState.Candidate,
                "cour", "RelativeSegment", 0, 0, 6, "Cour 2", 2)],
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Evangelion 2.22 You Can (Not) Advance")]
    [InlineData("Evangelion 1.11 You Are (Not) Alone")]
    [InlineData("Evangelion 3.33 You Can (Not) Redo")]
    [InlineData("Numbered Story 3.14 A New Chapter")]
    public void DecimalInsideCompleteSourceTitleBecomesReviewOnlyQuery(string token)
    {
        RuleTitleVariant variant = Variant(token);
        RuleSourceQueryEvidence.DecimalTitleQueries([variant]).Should().Equal(token);
        RuleParseResult rule = new(variant.Title, null, "unknown", null, null, null, 0.5, false, null,
            NamingEvidence: new(variant.Title, [], TitleVariants: [variant]));
        RuleSourceQueryEvidence.RequiresReview(rule, token).Should().BeTrue();
        RuleSourceQueryEvidence.RequiresReview(rule, variant.Title).Should().BeTrue(
            "清除数字标点也不能让未核实的原题关系变成自动身份依据");
    }

    [Theory]
    [InlineData("Show - 12.5 Extra Episode")]
    [InlineData("Show Episode 12.5 Special")]
    [InlineData("Show E12.5 Special")]
    [InlineData("Show v1.2 Final")]
    [InlineData("Show Version 1.2 Final")]
    [InlineData("Show Ver. 1.23 Final")]
    [InlineData("Show Episode: 12.5 Special")]
    [InlineData("Show Revision：1.23 Final")]
    [InlineData("Show 1.23-12 Anthology")]
    [InlineData("Show 1.23 – 12 Anthology")]
    [InlineData("Show 1.23 to 12 Anthology")]
    [InlineData("Show 12~14.23 Anthology")]
    [InlineData("Movie 5.1 ch Audio")]
    [InlineData("Movie DTS-HD(6.1ch,2.0ch)")]
    [InlineData("Evangelion 1.11-3.33")]
    [InlineData("1917")]
    [InlineData("1984")]
    [InlineData("Show 12.5")]
    [InlineData("Title 1920.1080 Pixels")]
    public void EpisodeVersionTechnicalAndCollectionNumbersAreNotNewWorkQueries(string token) =>
        RuleSourceQueryEvidence.DecimalTitleQueries([Variant(token)]).Should().BeEmpty();

    [Fact]
    public void RejectedEpisodeInterpretationDoesNotBecomeAWorkQuery()
    {
        RuleSourceQueryEvidence.DecimalTitleQueries([Variant("Show 12.5 Extra")], ["episode"]).Should().BeEmpty();
    }

    private static RuleTitleVariant Variant(string token) =>
        new(token.Replace('.', ' '), "Latin", null, "FileName", null, 0, token.Length, token, null);
}
