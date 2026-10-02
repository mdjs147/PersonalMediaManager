using System.Reflection;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed class ProcessFilePartialIdentityTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public void V2UnknownIdentitySearchPreservesLegacyOutcomeCompatibility(int version, bool expected)
    {
        AiParseRequest request = new("Example.mkv", Context: new(SchemaVersion: version));
        AiParseResult result = new("Example", null, "unknown", null, null, null, .9,
            RequiresIdentityVerification: true);
        AiCallOutcome validated = Validate(result, request);
        validated.Success.Should().Be(expected);
        validated.Result!.MediaType.Should().Be("unknown");
        validated.Result.IsAcceptable(.7).Should().BeFalse();
        if (version == 2) validated.Result.CanSearchForIdentity(.7).Should().BeTrue();
    }

    [Fact]
    public void PartialIdentitySearchCannotBypassBlockingSchemaError()
    {
        AiParseResult result = new("Example", null, "unknown", null, null, null, .9,
            Validation: new([], [], [], SchemaIssues: [new("$.confidence", "InvalidConfidence", "number", "object", true)]),
            RequiresIdentityVerification: true);
        AiCallOutcome validated = Validate(result, new("Example.mkv", Context: new(SchemaVersion: 2)));
        validated.Success.Should().BeFalse();
        validated.Result!.CanSearchForIdentity(.7).Should().BeFalse();
    }

    private static AiCallOutcome Validate(AiParseResult result, AiParseRequest request)
    {
        MethodInfo method = typeof(ProcessFileService).GetMethod("ValidateAiOutcome", BindingFlags.Static | BindingFlags.NonPublic)!;
        AiCallOutcome raw = new(true, result, 1, 1, null);
        RuleParseResult rule = new("Example", null, "unknown", null, null, null, .3, false, null);
        return (AiCallOutcome)method.Invoke(null, [raw, request, rule])!;
    }

    [Fact]
    public void ForcedTypeCannotWashOutUnresolvedRuleConflict()
    {
        AiParseRequest request = new("Example.mkv", Context: new(SchemaVersion: 2, RuleConflicts: ["type conflict"]));
        AiParseResult result = new("Example", null, "unknown", null, null, null, .9, RequiresIdentityVerification: true);
        RuleParseResult rule = new("Example", null, "movie", null, null, null, .3, false, null, ForceType: true);
        MethodInfo method = typeof(ProcessFileService).GetMethod("ValidateAiOutcome", BindingFlags.Static | BindingFlags.NonPublic)!;
        AiCallOutcome outcome = (AiCallOutcome)method.Invoke(null, [new AiCallOutcome(true, result, 1, 1, null), request, rule])!;
        outcome.Success.Should().BeFalse();
        outcome.Result!.MediaType.Should().Be("unknown");
        outcome.Result.IsAcceptable(0).Should().BeFalse();
    }

    [Fact]
    public void ForcedMovieCannotEraseExplicitEpisodeEvidenceAfterGuard()
    {
        AiParseRequest request = new("Example S03E01.mkv", RuleHintSeason: 3, RuleHintEpisode: 1, Context: new(SchemaVersion: 2));
        AiParseResult result = new("Example", null, "tv", 3, 1, null, .9);
        RuleParseResult rule = new("Example", null, "movie", null, null, null, .3, false, null, ForceType: true);
        MethodInfo method = typeof(ProcessFileService).GetMethod("ValidateAiOutcome", BindingFlags.Static | BindingFlags.NonPublic)!;
        AiCallOutcome outcome = (AiCallOutcome)method.Invoke(null, [new AiCallOutcome(true, result, 1, 1, null), request, rule])!;
        outcome.Success.Should().BeFalse();
        outcome.Result!.Season.Should().Be(3);
        outcome.Result.Episode.Should().Be(1);
    }
}

public sealed partial class ProcessFileServiceTests
{
    [Fact]
    public async Task PartialIdentityCannotSelectNamesakeByPopularityOrLanguage()
    {
        ConfigureRule(.3, false, year: null, title: "Example", mediaType: "unknown");
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new("Example", null, "unknown", null, null, null, .9,
                SearchAliases: ["OtherExample"], RequiresIdentityVerification: true), 1, 1, null));
        TmdbCandidate top = NewCandidate(42, "movie", "Example", 1990, 10000) with { OriginalLanguage = "zh", OriginCountry = ["CN"] };
        TmdbCandidate other = NewCandidate(43, "tv", "Example", 2024, 0) with { OriginalLanguage = "ja", OriginCountry = ["JP"] };
        TmdbCandidateScorer.CanAutoSelect(TmdbCandidateScorer.Rank([top, other], ["Example"], null,
            TmdbScoreWeights.Default, "zh-CN")).Should().BeTrue("反例必须具备可由热度和语言拉开的得分差");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([top, other], null));

        (await Run()).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().TmdbId.Should().BeNull();
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialIdentityWithOmittedEpisodeStillBlocksMovieAtFinalReconciliation()
    {
        ConfigureRule(.3, false, year: null, title: "Example", mediaType: "unknown");
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new("Example", null, "unknown", null, null, null, .9,
                RequiresIdentityVerification: true), 1, 1, null));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "movie", "Example")], null));
        string file = Path.Combine(Path.GetTempPath(), "Example S01E01.mkv");

        (await NewSut().ProcessAsync(new PendingFileItem(file, 0, PendingFileSource.Manual), CancellationToken.None))
            .Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().TmdbId.Should().BeNull();
        ReadTmdbReconciliation().GetProperty("typeDecision").GetProperty("reasonCode").GetString()
            .Should().Be("EpisodicFieldsTypeConflict");
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialIdentityWithUniqueSourceYearCanResolveThroughTmdb()
    {
        ConfigureRule(.3, false, year: 2024, title: "Example", mediaType: "unknown");
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new("Example", 2024, "unknown", null, null, null, .9,
                RequiresIdentityVerification: true), 1, 1, null));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([
                NewCandidate(42, "movie", "Example", 2024, 10000) with { OriginalLanguage = "zh", OriginCountry = ["CN"] },
                NewCandidate(43, "tv", "Example", 1990, 0) with { OriginalLanguage = "ja", OriginCountry = ["JP"] }], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Movies/Example (2024).mkv");

        (await NewSut().ProcessAsync(new PendingFileItem(Path.Combine(Path.GetTempPath(), "Example.2024.mkv"),
            0, PendingFileSource.Manual), CancellationToken.None)).Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().TmdbId.Should().Be(42);
        ReadOne().TmdbMediaType.Should().Be("movie");
    }
}
