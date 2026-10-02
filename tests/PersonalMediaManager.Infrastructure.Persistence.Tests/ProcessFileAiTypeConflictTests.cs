using System.Reflection;
using System.Text.Json;
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

public sealed partial class ProcessFileServiceTests
{
    [Fact]
    public async Task AiMovieConflictStopsBeforeMovieSearchAndPreservesRuleFieldsInAudit()
    {
        ConfigureRule(.3, false, year: null, title: "Example", season: 3, episode: 1, mediaType: "tv");
        ConfigureAi(true, title: "Example", mediaType: "movie");

        (await Run()).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().TmdbId.Should().BeNull();
        ReadOne().ErrorMessage.Should().Contain("季集证据冲突");
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string detail = db.ProcessSteps.Where(s => s.Stage == MediaItemStatus.AiParsing)
            .Select(s => s.Detail).AsEnumerable().Last(s => s?.Contains("\"validation\"", StringComparison.Ordinal) == true)!;
        using JsonDocument audit = JsonDocument.Parse(detail);
        audit.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        audit.RootElement.GetProperty("output").GetProperty("season").GetInt32().Should().Be(3);
        audit.RootElement.GetProperty("output").GetProperty("episode").GetInt32().Should().Be(1);
        audit.RootElement.GetProperty("validation").GetProperty("reasonCodes").EnumerateArray()
            .Select(v => v.GetString()).Should().Contain("EpisodicFieldsTypeConflict");
    }

    [Fact]
    public async Task LiteralEpisodeWithoutRuleHintsStopsMovieBeforeTmdbRematch()
    {
        ConfigureRule(.3, false, year: null, title: "Example", mediaType: "unknown");
        ConfigureAi(true, title: "Example", mediaType: "movie");
        string file = Path.Combine(Path.GetTempPath(), "Example 第 四 季 第 3 集.mkv");

        (await NewSut().ProcessAsync(new PendingFileItem(file, 0, PendingFileSource.Manual), CancellationToken.None))
            .Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AiCanCorrectWeakTvTypeWhenNoEpisodicEvidenceExists()
    {
        ConfigureRule(.3, false, year: null, title: "Example", mediaType: "tv");
        ConfigureAi(true, title: "Example", mediaType: "movie");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "movie", "Example")], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Movies/Example.mkv");

        (await Run()).Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().TmdbMediaType.Should().Be("movie");
    }

    [Fact]
    public void ClosedCandidateConflictRemainsRejectedAfterProductionRevalidation()
    {
        AiParseRequest request = new("Example S03E01.mkv", RuleHintSeason: 3, RuleHintEpisode: 1,
            Context: new(TaskType: AiParseTaskType.DisambiguateCandidates,
                Candidates: [new(42, "movie", "Example")]));
        AiCallOutcome raw = new(true, new("Example", null, "movie", null, null, null, 1,
            SelectedCandidateId: 42), 1, 1, null);
        RuleParseResult rule = new("Example", null, "tv", 3, 1, null, .3, false, null);
        MethodInfo validate = typeof(ProcessFileService).GetMethod("ValidateAiOutcome", BindingFlags.Static | BindingFlags.NonPublic)!;
        AiCallOutcome once = (AiCallOutcome)validate.Invoke(null, [raw, request, rule])!;
        AiCallOutcome twice = (AiCallOutcome)validate.Invoke(null, [once, request, rule])!;
        twice.Success.Should().BeFalse();
        twice.Result.Should().NotBeNull();
        twice.Result!.Abstained.Should().BeTrue();
        twice.Result.SelectedCandidateId.Should().BeNull();
        twice.Result.Season.Should().Be(3);
        twice.Result.Episode.Should().Be(1);
    }
}
