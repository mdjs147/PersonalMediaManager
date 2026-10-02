using NSubstitute;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class AiTypeEvidenceTests
{
    [Theory]
    [InlineData("Fate Zero 21 [BD 1920x1080].mkv")]
    [InlineData("Example.2024.1080p.mkv")]
    [InlineData("The Movie Critic.mkv")]
    [InlineData("Example The Movie.mkv")]
    public void UnsupportedMovieTypeBecomesSearchableUnknownWithoutGuessingEpisode(string file)
    {
        string title = file.StartsWith("Fate", StringComparison.Ordinal) ? "Fate Zero 21"
            : file.StartsWith("The Movie", StringComparison.Ordinal) ? "The Movie Critic" : "Example";
        AiParseResult result = AiParseResultGuard.Validate(new(title, null, "movie", null, null, null, .9),
            new(file, RuleHintType: "movie", Context: new(SchemaVersion: 2)));
        result.MediaType.Should().Be("unknown");
        result.Episode.Should().BeNull();
        result.Abstained.Should().BeFalse();
        result.IsAcceptable(.7).Should().BeFalse();
        result.CanSearchForIdentity(.7).Should().BeTrue();
        result.Validation!.ReasonCodes.Should().Contain("TypeEvidenceMissing");
    }

    [Theory]
    [InlineData("[剧场版] Example.mkv", null)]
    [InlineData("Example The Movie.mkv", "Movies")]
    public void IndependentReleaseEvidenceSupportsMovieHint(string file, string? folder)
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Example", null, "movie", null, null, null, .9),
            new(file, folder, Context: new(SchemaVersion: 2)));
        result.IsAcceptable(.7).Should().BeTrue();
        result.RequiresIdentityVerification.Should().BeFalse();
    }

    [Fact]
    public void FilmWordInsideEpisodicTitleCannotOverrideEpisodeEvidence()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("The Movie Critic", null, "movie", null, null, null, .9),
            new("The Movie Critic S01E01.mkv", "Movies", Context: new(SchemaVersion: 2)));
        result.Abstained.Should().BeTrue();
        result.CanSearchForIdentity(0).Should().BeFalse();
    }

    [Theory]
    [InlineData("Example S03E01.mkv")]
    [InlineData("Example 第三季 第1集.mkv")]
    public void ExplicitEpisodicEvidenceRetainsTv(string file)
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Example", null, "tv", 3, 1, null, .9),
            new(file, Context: new(SchemaVersion: 2)));
        result.IsAcceptable(.7).Should().BeTrue();
        result.Season.Should().Be(3);
        result.Episode.Should().Be(1);
    }

    [Fact]
    public void SelectedCandidateTypeIsEvidenceEvenWhenFilenameHasNoTypeMarker()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Example", null, "movie", null, null, null, .9,
            SelectedCandidateId: 42), new("Example.mkv", Context: new(SchemaVersion: 2,
            TaskType: AiParseTaskType.DisambiguateCandidates, Candidates: [new(42, "movie", "Example")])));
        result.MediaType.Should().Be("movie");
        result.IsAcceptable(.7).Should().BeTrue();
    }

    [Fact]
    public void TypeUncertaintyDoesNotBypassExplicitRuleConflicts()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Example", null, "movie", null, null, null, .9),
            new("Example.mkv", Context: new(SchemaVersion: 2, RuleConflicts: ["type: conflicting sources"])));
        result.MediaType.Should().Be("unknown");
        result.CanSearchForIdentity(.7).Should().BeFalse();
    }

    [Fact]
    public void UnknownTypeDoesNotPermitUngroundedTitleSearch()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Invented Title", null, "movie", null, null, null, .9),
            new("Example.mkv", Context: new(SchemaVersion: 2)));
        result.CanSearchForIdentity(.7).Should().BeFalse();
        result.Validation!.ReasonCodes.Should().Contain("UnsupportedSearchTitle");
    }

    [Fact]
    public void InvalidConfidenceCannotBecomeIdentityAcceptanceOrSearch()
    {
        AiParseResult raw = new("Example", null, "movie", null, null, null, 100);
        raw.IsAcceptable(.7).Should().BeFalse();
        AiParseResult guarded = AiParseResultGuard.Validate(raw, new("Example.mkv", Context: new(SchemaVersion: 2)));
        guarded.Confidence.Should().Be(0);
        guarded.Abstained.Should().BeTrue();
        guarded.CanSearchForIdentity(0).Should().BeFalse();
    }

    [Fact]
    public void HexIdentifierPrefixIsNotAnEpisodeMarker()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Example", null, "movie", null, null, null, .9),
            new("pmm-pfs-e123abcd.mkv", Context: new()));
        result.Abstained.Should().BeFalse();
        AiParseResultGuard.HasEpisodicSourceEvidence(["pmm-pfs-e123abcd.mkv"]).Should().BeFalse();
    }

    [Fact]
    public void NamesakeDetectionCrossesLocalizedAndOriginalTitleFields()
    {
        AiCandidateEvidence localized = new(42, "movie", "示例", "Example", 2024, 1);
        AiCandidateEvidence other = new(43, "tv", "Example", null, 1990, .1);
        MediaIdentityEvidence.HasUnresolvedNamesake(localized, [localized, other], ["Example.mkv"]).Should().BeTrue();
        MediaIdentityEvidence.HasUnresolvedNamesake(localized, [localized, other], ["Example.2024.mkv"]).Should().BeFalse();
    }
}

public sealed partial class AiCallOrchestratorTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task UnknownPartialSearchIsExplicitV2Only(int version, bool canContinue)
    {
        AiParseResult partial = new("Example", null, "unknown", null, null, null, .9,
            Validation: new([], ["type"], ["TypeEvidenceMissing"]), RequiresIdentityVerification: true);
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [partial] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut([primary], [Resolution(1, AiProviderType.Ollama, true)], audit);
        AiCallOutcome result = await sut.ExecuteAsync(new("Example.mkv", Context: new(SchemaVersion: version)), null);
        result.Success.Should().Be(canContinue);
        if (canContinue)
        {
            result.Result!.IsAcceptable(.7).Should().BeFalse();
            result.Result.CanSearchForIdentity(.7).Should().BeTrue();
            await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(entry => entry.Success
                && entry.ErrorDetail != null && entry.ErrorDetail.Contains("待 TMDB 核验")), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task V2PartialSearchCannotBypassAbstentionOrBlockingSchema(bool abstained, bool schemaError)
    {
        AiParseResult partial = new("Example", null, "unknown", null, null, null, .9, Abstained: abstained,
            Validation: new([], [], [], SchemaIssues: schemaError
                ? [new("$.confidence", "InvalidConfidence", "number[0,1]", "number", true)] : null),
            RequiresIdentityVerification: true);
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [partial] };
        AiCallOrchestrator sut = NewSut([primary], [Resolution(1, AiProviderType.Ollama, true)], Substitute.For<IAuditAiCallWriter>());
        AiCallOutcome outcome = await sut.ExecuteAsync(new("Example.mkv", Context: new(SchemaVersion: 2)), null);
        outcome.Success.Should().BeFalse();
        outcome.Attempts!.Single().ErrorType.Should().Be(schemaError ? "Logical" : "UnknownEvidence");
    }
}
