using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class AiEpisodicTypeConflictTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MovieClaimCannotEraseConfirmedRuleSeasonAndEpisode(int version)
    {
        AiParseRequest request = new("[ANi] GRAND BLUE 碧藍之海 3 - 01 [1080P].mp4",
            RuleHintType: "tv", RuleHintSeason: 3, RuleHintEpisode: 1,
            Context: new(SchemaVersion: version, RuleProvenance:
                [new("season", 3, "ConfirmedSeriesNumbering"), new("episode", 1, "UserRule")]));
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"GRAND BLUE 碧藍之海","type":"movie","season":null,"episode":1,"confidence":0.85}
            """, request);
        result.Abstained.Should().BeTrue();
        result.IsAcceptable(0).Should().BeFalse();
        result.Confidence.Should().Be(0);
        result.Season.Should().Be(3);
        result.Episode.Should().Be(1);
        result.Validation!.RejectedFields.Should().Contain("type");
        result.Validation.ReasonCodes.Should().Contain("EpisodicFieldsTypeConflict");
        AiParseResult second = AiParseResultGuard.Validate(result, request);
        second.Abstained.Should().BeTrue();
        second.Season.Should().Be(3);
        second.Episode.Should().Be(1);
    }

    [Theory]
    [InlineData("Example S04E03.mkv")]
    [InlineData("Example 第 四 季 第 3 集.mkv")]
    public void LiteralEpisodicEvidenceBlocksMovieEvenWithoutRuleHints(string file)
    {
        const string json = """{"title":"Example","type":"movie","season":4,"episode":3,"confidence":1}""";
        AiParseRequest request = new(file, Context: new(SchemaVersion: 2));
        AiParseResult before = AiPromptHelpers.ParseTaskContentBeforeGuard(json, request);
        before.Season.Should().Be(4);
        before.Episode.Should().Be(3);
        AiParseResult result = AiPromptHelpers.ParseTaskContent(json, request);
        result.Abstained.Should().BeTrue();
        result.Season.Should().Be(4);
        result.Episode.Should().Be(3);
        AiParseResult second = AiParseResultGuard.Validate(result, request);
        second.Abstained.Should().BeTrue();
        second.Season.Should().Be(4);
        second.Episode.Should().Be(3);
    }

    [Theory]
    [InlineData("Example S04E03.mkv")]
    [InlineData("Example 4th Season.mkv")]
    [InlineData("Example 第 四 季 第 3 集.mkv")]
    public void OmittingModelEpisodeFieldsCannotHideLiteralConflict(string file)
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"title":"Example","type":"movie","confidence":1}""",
            new(file, Context: new(SchemaVersion: 2)));
        result.Abstained.Should().BeTrue();
        result.IsAcceptable(0).Should().BeFalse();
    }

    [Theory]
    [InlineData("Movie.2024.mkv")]
    [InlineData("Movie 3 - 01.mkv")]
    [InlineData("Movie 1920x1080.mkv")]
    [InlineData("MovieE01-02.mkv")]
    [InlineData("Movie 11st Season.mkv")]
    public void WeakTvHintAndBareNumbersDoNotForceTvIdentity(string file)
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Movie","type":"movie","season":1,"episode":1,"confidence":1}
            """, new(file, RuleHintType: "tv", Context: new(SchemaVersion: 2)));
        result.Abstained.Should().BeFalse();
        result.MediaType.Should().Be("unknown");
        result.IsAcceptable(.8).Should().BeFalse();
        result.CanSearchForIdentity(.8).Should().BeTrue();
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Fact]
    public void RejectedRuleEvidenceAloneDoesNotCreateTypeConflict()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Movie", null, "movie", null, null, null, 1),
            new("Movie.mkv", Context: new(RuleProvenance: [new("season", 3, "RuleRejected", Rejected: true)])));
        result.IsAcceptable(.8).Should().BeTrue();
    }

    [Fact]
    public void ExplicitParentSeasonAlsoBlocksMovieClaim()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"title":"Example","type":"movie","confidence":1}""",
            new("Example.mkv", RelativeSegments: ["Example", "Season 3"], Context: new(SchemaVersion: 2)));
        result.Abstained.Should().BeTrue();
    }

    [Fact]
    public void ConsistentLockedMovieIdentityRemainsAccepted()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"title":"Movie","type":"movie","confidence":1}""",
            new("Movie.mkv", Context: new(SchemaVersion: 2, TaskType: AiParseTaskType.FillMissingFields,
                LockedBinding: new(42, "movie", "Movie"))));
        result.IsAcceptable(.8).Should().BeTrue();
        result.SelectedCandidateId.Should().Be(42);
    }

    [Fact]
    public void ConflictingClosedCandidateCannotRemainSelected()
    {
        AiParseRequest request = new("Example S03E01.mkv", RuleHintSeason: 3, RuleHintEpisode: 1,
            Context: new(SchemaVersion: 2, TaskType: AiParseTaskType.DisambiguateCandidates,
                Candidates: [new(42, "movie", "Example", null, null, 1)]));
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"movie","selectedCandidateId":42,"confidence":1}
            """, request);
        result.Abstained.Should().BeTrue();
        result.SelectedCandidateId.Should().BeNull();
        result.Season.Should().Be(3);
        result.Episode.Should().Be(1);
    }

    [Fact]
    public void NamesakeAbstentionStillRejectsUngroundedEpisodeClaimsOnFirstPass()
    {
        AiParseRequest request = new("Example.mkv", Context: new(SchemaVersion: 2,
            TaskType: AiParseTaskType.DisambiguateCandidates,
            Candidates: [new(42, "movie", "Example", Year: 1990), new(43, "movie", "Example", Year: 2024)]));
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"movie","selectedCandidateId":42,"season":4,"episode":3,"confidence":1}
            """, request);
        result.Abstained.Should().BeTrue();
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
        result.Validation!.ReasonCodes.Should().Contain("AmbiguousCandidateIdentity");
        AiParseResult second = AiParseResultGuard.Validate(result, request);
        second.Season.Should().BeNull();
        second.Episode.Should().BeNull();
    }

    [Fact]
    public void LockedTvIdentityRemainsAuthoritative()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"movie","season":3,"episode":1,"confidence":1}
            """, new("Example S03E01.mkv", Context: new(SchemaVersion: 2,
                TaskType: AiParseTaskType.FillMissingFields, LockedBinding: new(42, "tv", "Example", null, 3, 1))));
        result.MediaType.Should().Be("tv");
        result.Abstained.Should().BeFalse();
        result.SelectedCandidateId.Should().Be(42);
        result.Season.Should().Be(3);
    }

    [Fact]
    public void LegacyParserStillClearsUnvalidatedMovieEpisodeClaims()
    {
        AiParseResult result = AiPromptHelpers.ParseContent("""
            {"title":"Example","type":"movie","season":3,"episode":1,"confidence":1}
            """);
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }
}
