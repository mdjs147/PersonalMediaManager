using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.LocalAi;
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
    public async Task LocalBeforeRulesRunsFirstButCannotOverrideSuccessfulRuleIdentity()
    {
        ILocalMediaAssistService local = Substitute.For<ILocalMediaAssistService>();
        List<string> order = [];
        local.SuggestAsync(Arg.Any<FileParseContext>(), null, LocalAiMode.BeforeRules, Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("local"); return LocalSuggestions(LocalAiMode.BeforeRules); });
        ConfigureRule(0.9, false);
        _ruleEngine.When(r => r.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("rule"));
        ConfigureTmdb(1);
        ConfigureClassify(ClassifyDecision.Matched, 7);
        ConfigureArchive(ArchiveOutcome.Completed, "/M/Example.mkv");
        ProcessFileOutcome outcome = await RunWithLocal(local);
        outcome.Outcome.Should().Be(ProcessOutcome.Completed);
        order.Should().Equal("local", "rule");
        ReadOne().TmdbId.Should().Be(100);
        ReadOne().ParseSource.Should().Be(ParseSource.Rule);
        await local.DidNotReceive().LookupAsync(Arg.Any<LocalMediaAssistResult>(), Arg.Any<IReadOnlyList<TmdbSearchRequest>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await local.DidNotReceive().SuggestAsync(Arg.Any<FileParseContext>(), Arg.Any<RuleParseResult>(),
            LocalAiMode.AfterRules, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(LocalAiMode.BeforeRules)]
    [InlineData(LocalAiMode.AfterRules)]
    public async Task LocalCandidatesRemainUnboundAndPreserveRuleFieldsForReview(LocalAiMode mode)
    {
        ILocalMediaAssistService local = Substitute.For<ILocalMediaAssistService>();
        ConfigureRule(0.2, true);
        RuleParseResult original = new("Original Programme", 2024, "tv", 3, 1, null, 0.2, true, null);
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(original);
        local.SuggestAsync(Arg.Any<FileParseContext>(), Arg.Any<RuleParseResult?>(), Arg.Any<LocalAiMode>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<LocalAiMode>() == mode ? LocalSuggestions(mode)
                : new LocalMediaAssistResult(mode, "NotScheduled", [], []));
        TmdbCandidate tv = new(123, "tv", "Model Hypothesis", null, null, 99999, null, null, null, null);
        TmdbCandidate movie = tv with { MediaType = "movie" };
        local.LookupAsync(Arg.Any<LocalMediaAssistResult>(), Arg.Any<IReadOnlyList<TmdbSearchRequest>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new LocalMediaLookupResult([tv, movie], [], false, false));
        ProcessFileOutcome outcome = await RunWithLocal(local,
            fullPath: Path.Combine(Path.GetTempPath(), "Original Programme.2024.S03E01.mkv"));
        outcome.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem saved = ReadOne();
        saved.TmdbId.Should().BeNull();
        saved.TmdbMediaType.Should().BeNull();
        ParsedInfo parsed = ParsedInfo.FromJson(saved.ParsedInfo)!;
        parsed.Title.Should().Be(original.Title);
        parsed.Type.Should().Be("tv");
        parsed.Year.Should().Be(2024);
        parsed.Season.Should().Be(3);
        parsed.Episode.Should().Be(1);
        using JsonDocument candidates = JsonDocument.Parse(saved.TmdbCandidatesJson!);
        candidates.RootElement.GetArrayLength().Should().Be(2, "同数值的电影/剧集不是一个身份");
        await _classify.DidNotReceive().ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalServiceFailureFallsBackWithoutFailingGoodRulePipeline()
    {
        ILocalMediaAssistService local = Substitute.For<ILocalMediaAssistService>();
        local.SuggestAsync(Arg.Any<FileParseContext>(), Arg.Any<RuleParseResult?>(), Arg.Any<LocalAiMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("runtime stopped"));
        ConfigureRule(0.9, false); ConfigureTmdb(1);
        ConfigureClassify(ClassifyDecision.Matched, 7); ConfigureArchive(ArchiveOutcome.Completed, "/M/Example.mkv");
        (await RunWithLocal(local)).Outcome.Should().Be(ProcessOutcome.Completed);
        ReadOne().ParseSource.Should().Be(ParseSource.Rule);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalReviewSnapshotCannotLoseMovieBehindTwentyTvCandidates(bool hasExistingCandidates)
    {
        ILocalMediaAssistService local = Substitute.For<ILocalMediaAssistService>();
        ConfigureRule(hasExistingCandidates ? 0.9 : 0.2, !hasExistingCandidates, year: null);
        TmdbCandidate[] television = Enumerable.Range(1, 20)
            .Select(id => new TmdbCandidate(id, "tv", "Unrelated", null, null, 999, null, null, null, null)).ToArray();
        TmdbCandidate movie = new(1, "movie", "New Alias", null, null, 0, null, null, null, null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(hasExistingCandidates ? television : [], null));
        local.SuggestAsync(Arg.Any<FileParseContext>(), Arg.Any<RuleParseResult?>(), Arg.Any<LocalAiMode>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LocalAiMode>() == LocalAiMode.AfterRules ? LocalSuggestions(LocalAiMode.AfterRules)
                : new LocalMediaAssistResult(LocalAiMode.AfterRules, "NotScheduled", [], []));
        local.LookupAsync(Arg.Any<LocalMediaAssistResult>(), Arg.Any<IReadOnlyList<TmdbSearchRequest>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new LocalMediaLookupResult(
                hasExistingCandidates ? [movie] : [.. television, movie], [], false, false));
        (await RunWithLocal(local)).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        using JsonDocument snapshot = JsonDocument.Parse(ReadOne().TmdbCandidatesJson!);
        snapshot.RootElement.GetArrayLength().Should().Be(21);
        snapshot.RootElement.EnumerateArray().Should().Contain(c => c.GetProperty("tmdbId").GetInt32() == 1
            && c.GetProperty("mediaType").GetString() == "movie");
        snapshot.RootElement.EnumerateArray().Should().Contain(c => c.GetProperty("tmdbId").GetInt32() == 1
            && c.GetProperty("mediaType").GetString() == "tv");
    }

    [Fact]
    public async Task LocalCancellationIsNotSwallowedAsFallback()
    {
        ILocalMediaAssistService local = Substitute.For<ILocalMediaAssistService>();
        using CancellationTokenSource ct = new();
        local.SuggestAsync(Arg.Any<FileParseContext>(), Arg.Any<RuleParseResult?>(), Arg.Any<LocalAiMode>(), Arg.Any<CancellationToken>())
            .Returns(_ => { ct.Cancel(); return Task.FromCanceled<LocalMediaAssistResult>(ct.Token); });
        Func<Task> action = () => RunWithLocal(local, ct.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        await _ruleEngine.DidNotReceive().ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>());
    }

    private static LocalMediaAssistResult LocalSuggestions(LocalAiMode mode) => new(mode, "Validated",
        [new("Model Hypothesis", "Original Programme", "FileName", null, 0, 18, "source", "UnverifiedAlias")], [],
        InferenceAttempted: true);

    private Task<ProcessFileOutcome> RunWithLocal(ILocalMediaAssistService local, CancellationToken ct = default, string? fullPath = null)
    {
        ProcessFileService sut = new(_dbFactory, _writeDetector, _ruleEngine, _forcedMatch, _tmdb, _aiOrchestrator,
            _folderCache, _classify, _archive, _fileHasher, _fileProbe, _audioProbe, new NullTaskNotifier(), _webhook,
            new SystemClock(), NullLogger<ProcessFileService>.Instance, local);
        return sut.ProcessAsync(new PendingFileItem(fullPath ?? _tempFile, 1, PendingFileSource.Watcher), ct);
    }
}
