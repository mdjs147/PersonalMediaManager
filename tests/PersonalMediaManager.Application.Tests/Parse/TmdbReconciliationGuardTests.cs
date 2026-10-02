using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class TmdbReconciliationGuardTests
{
    private static TmdbDetailsResult Details(int? total = 1, IReadOnlyList<TmdbSeasonInfo>? seasons = null,
        int id = 42, string type = "tv") => new(id, type, "Example", "Example", 2024, total,
            null, null, null, null, null, "{}", seasons);

    [Fact]
    public void SingleRegularSeasonAndSpecials_WithMatchingRange_AllowsS1()
    {
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(
            Details(seasons: [new(0, 3), new(1, 12)]), 42, 11, 12).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "CatalogueUnknown")]
    [InlineData(0, "EpisodeCountUnknown")]
    [InlineData(2, "EpisodeOutsideCatalogue")]
    public void SingleSeasonSummaryAloneOrBadEpisodeCount_CannotInfer(int? count, string reason)
    {
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(
            Details(seasons: count.HasValue ? [new(1, count.Value)] : null), 42, 3, null).Should().Be(reason);
    }

    [Theory]
    [InlineData(null, "TotalSeasonsUnknown")]
    [InlineData(2, "NotSingleSeason")]
    public void SummaryMustConfirmOneRegularSeason(int? total, string reason)
    {
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(
            Details(total, [new(1, 12)]), 42, 1, null).Should().Be(reason);
    }

    [Fact]
    public void CatalogueMustContainOneRegularS1AndNoDuplicateOrConflictingEntries()
    {
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(Details(seasons: [new(0, 3)]), 42, 1, null)
            .Should().Be("SingleSeasonCatalogueMismatch");
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(Details(seasons: [new(2, 12)]), 42, 1, null)
            .Should().Be("FirstSeasonAbsent");
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(Details(seasons: [new(1, 12), new(2, 12)]), 42, 1, null)
            .Should().Be("SingleSeasonCatalogueMismatch");
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(Details(seasons: [new(1, 12), new(1, 12)]), 42, 1, null)
            .Should().Be("SeasonCatalogueConflict");
    }

    [Theory]
    [InlineData(99, "tv")]
    [InlineData(42, "movie")]
    public void CatalogueIdentityIsTypedAndMustMatch(int id, string type)
    {
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(Details(seasons: [new(1, 12)], id: id, type: type), 42, 1, null)
            .Should().Be("CatalogueIdentityMismatch");
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(13, null)]
    [InlineData(11, 13)]
    [InlineData(11, 10)]
    public void AllSourceEpisodeBoundsMustPass(int episode, int? end)
    {
        TmdbEpisodeCatalogueGuard.ValidateSingleSeasonInference(Details(seasons: [new(1, 12)]), 42, episode, end)
            .Should().Be("EpisodeOutsideCatalogue");
    }

    private static RuleParseResult Rule(string type, int? season = null, int? episode = null, bool forced = false)
        => new("Example", null, type, season, episode, null, .9, false, 1, ForceType: forced);

    [Theory]
    [InlineData("unknown", "movie", "CandidateTypeResolved")]
    [InlineData("unknown", "tv", "CandidateTypeResolved")]
    [InlineData("tv", "movie", "InferredTypeCorrected")]
    [InlineData("movie", "tv", "InferredTypeCorrected")]
    [InlineData("tv", "tv", "CandidateTypeConfirmed")]
    public void WeakOrUnknownTypeCanBeResolvedWithRecordedReason(string before, string after, string reason)
    {
        TmdbMediaTypeDecision d = TmdbMediaTypeGuard.Reconcile(Rule(before), null, after, ["Example.mkv"]);
        d.ExtractedType.Should().Be(before);
        d.ResolvedType.Should().Be(after);
        d.ReasonCode.Should().Be(reason);
        d.RequiresReview.Should().BeFalse();
    }

    [Fact]
    public void ExplicitEpisodesAndForcedTypesCannotBeSilentlyOverwritten()
    {
        TmdbMediaTypeGuard.Reconcile(Rule("tv", 1, 2), null, "movie", ["Example.S01E02.mkv"])
            .Should().Be(new TmdbMediaTypeDecision("tv", "movie", "EpisodicFieldsTypeConflict", true));
        TmdbMediaTypeGuard.Reconcile(Rule("movie", forced: true), null, "tv", ["Example.mkv"])
            .ReasonCode.Should().Be("ForcedTypeConflict");
    }

    [Theory]
    [InlineData("Example 劇場版.mkv", true)]
    [InlineData("Example The.Movie.mkv", false)]
    [InlineData("Example BDRip.mkv", false)]
    [InlineData("Film Stars (2024).mkv", false)]
    public void MovieMarkerIsDifferentFromTechnicalTagsAndTitleWords(string name, bool review)
    {
        TmdbMediaTypeGuard.Reconcile(Rule("movie"), null, "tv", [name]).RequiresReview.Should().Be(review);
    }

    [Fact]
    public void UserForcedIdentityRemainsAuthoritative()
    {
        TmdbMediaTypeGuard.Reconcile(Rule("tv", 1, 2), null, "movie", ["Example.mkv"], forcedMatch: true)
            .ReasonCode.Should().Be("ManualIdentityType");
    }

    [Fact]
    public void UnknownPartialWithoutEpisodeValuesStillRetainsSourceTypeConstraint()
    {
        AiParseResult partial = new("Example", null, "unknown", null, null, null, .9, RequiresIdentityVerification: true);
        TmdbMediaTypeGuard.Reconcile(Rule("unknown"), partial, "movie", ["Example S01E01.mkv"])
            .ReasonCode.Should().Be("EpisodicFieldsTypeConflict");
    }

    [Theory]
    [InlineData("The Movie Critic.mkv", false)]
    [InlineData("Example The.Movie.mkv", false)]
    public void TheMovieWordsAloneCannotRejectVerifiedTv(string file, bool review)
    {
        TmdbMediaTypeGuard.Reconcile(Rule("unknown"), null, "tv", [file]).RequiresReview.Should().Be(review);
        TmdbMediaTypeGuard.Reconcile(Rule("unknown"), null, "tv", [file, "Movies"])
            .ReasonCode.Should().Be("MovieMarkerTypeConflict");
    }

    [Fact]
    public void ExplicitParentReleaseMarkerStillRejectsTvCandidate()
    {
        TmdbMediaTypeGuard.Reconcile(Rule("unknown"), null, "tv", ["Example.mkv", "[劇場版] Example"])
            .ReasonCode.Should().Be("MovieMarkerTypeConflict");
    }
}
