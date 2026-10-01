using PersonalMediaManager.Application.Dtos.Dashboard;
using PersonalMediaManager.Application.Dtos.History;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed class CompletionProvenanceTests
{
    private static ProcessStepEntry Step(long id, MediaItemStatus stage, string? detail) =>
        new(id, stage, DateTimeOffset.UnixEpoch.AddSeconds(id), 0, detail);

    [Fact]
    public void LatestCompletion_OverridesEarlierConfirmation_ButRetainsIntervention()
    {
        CompletionProvenance result = CompletionProvenanceProjection.Project(MediaItemStatus.Completed, ParseSource.Rule,
        [Step(1, MediaItemStatus.AwaitingReview, null), Step(2, MediaItemStatus.Completed, "{\"confirm\":true}"),
         Step(3, MediaItemStatus.Queued, "{\"autoRetry\":true}"),
         Step(4, MediaItemStatus.Completed, "{\"provenanceVersion\":1,\"completionRoute\":\"AutomaticPipeline\"}")]);
        result.Category.Should().Be("AutomaticPipeline");
        result.EverReviewed.Should().BeTrue();
        result.EverConfirmed.Should().BeTrue();
        result.AutomaticRetry.Should().BeTrue();
        result.ExplicitCorrection.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("broken")]
    [InlineData("[]")]
    [InlineData("{\"confirm\":true,\"provenanceVersion\":1,\"completionRoute\":\"AutomaticPipeline\"}")]
    [InlineData("{\"provenanceVersion\":\"1\",\"completionRoute\":\"AutomaticPipeline\"}")]
    public void MissingOrInvalidEvidence_IsUnknown(string? detail)
    {
        CompletionProvenanceProjection.Project(MediaItemStatus.Completed, ParseSource.Rule,
            [Step(1, MediaItemStatus.Completed, detail)]).Category.Should().Be("Unknown");
        CompletionProvenanceProjection.Project(MediaItemStatus.Completed, ParseSource.Rule, []).Category.Should().Be("Unknown");
    }

    [Fact]
    public void EqualTimestamps_UseIdTieBreaker_AndLegacyRetryStaysUnknown()
    {
        ProcessStepEntry first = new(1, MediaItemStatus.Completed, DateTimeOffset.UnixEpoch, 0, "{\"confirm\":true}");
        ProcessStepEntry last = new(2, MediaItemStatus.Completed, DateTimeOffset.UnixEpoch, 0, "{\"manual\":true}");
        CompletionProvenanceProjection.Project(MediaItemStatus.Completed, null, [last, first]).Category.Should().Be("ManualArchive");
        CompletionProvenance legacy = CompletionProvenanceProjection.Project(MediaItemStatus.Completed, ParseSource.Rule,
            [Step(1, MediaItemStatus.AwaitingReview, "{}"), Step(2, MediaItemStatus.Queued, "{\"autoRetry\":true}"),
             Step(3, MediaItemStatus.Completed, "{}")]);
        legacy.Category.Should().Be("Unknown");
        legacy.EverReviewed.Should().BeTrue();
        legacy.EverConfirmed.Should().BeFalse();
        legacy.AutomaticRetry.Should().BeTrue();
    }

    [Fact]
    public void LatestMalformedCompletion_DoesNotReuseEarlierMarker()
    {
        CompletionProvenanceProjection.Project(MediaItemStatus.Completed, null,
            [Step(1, MediaItemStatus.Completed, "{\"confirm\":true}"), Step(2, MediaItemStatus.Completed, "broken")])
            .Category.Should().Be("Unknown");
    }

    [Fact]
    public void Confirmation_IsNotCorrection_AndParseSourceIsNotCompletionRoute()
    {
        CompletionProvenance result = CompletionProvenanceProjection.Project(MediaItemStatus.Completed, ParseSource.Manual,
            [Step(1, MediaItemStatus.Completed, "{\"confirm\":true}")]);
        result.Category.Should().Be("Confirmed");
        result.ExplicitCorrection.Should().BeFalse();
        result.ForcedAnchor.Should().BeTrue();
        result.ManualArchive.Should().BeFalse();
        CompletionProvenanceProjection.Project(MediaItemStatus.Completed, ParseSource.Hybrid, []).FolderReuse.Should().BeFalse();
    }

    [Fact]
    public void ManualArchiveAndCorrection_HaveIndependentEvidence()
    {
        CompletionProvenance result = CompletionProvenanceProjection.Project(MediaItemStatus.Completed, null,
            [Step(1, MediaItemStatus.AwaitingReview, "{\"explicitCorrection\":true}"),
             Step(2, MediaItemStatus.TmdbMatching, "{\"source\":\"reuse\"}"),
             Step(3, MediaItemStatus.Completed, "{\"manual\":true}")]);
        result.Category.Should().Be("ManualArchive");
        result.ExplicitCorrection.Should().BeTrue();
        result.FolderReuse.Should().BeTrue();
    }

    [Theory]
    [InlineData(MediaItemStatus.Ignored)]
    [InlineData(MediaItemStatus.Failed)]
    [InlineData(MediaItemStatus.Skipped)]
    public void NonCompleted_IsExcludedEvenWithOlderCompletion(MediaItemStatus status)
    {
        CompletionProvenance result = CompletionProvenanceProjection.Project(status, null,
            [Step(1, MediaItemStatus.Completed, "{\"confirm\":true}")]);
        CompletionProvenanceProjection.Aggregate([result]).Completed.Should().Be(0);
    }
}
