using System.Text.Json;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ProcessFileServiceTests
{
    [Fact]
    public async Task EditionEpisodeGroupStartOnlyCannotAuthorizeAnUnmappedRangeEnd()
    {
        RuleParseResult rule = new("Example HD Remaster", null, "tv", null, 1, 2, 0.99, false, null,
            NamingEvidence: new("Example HD Remaster", ["HD Remaster"],
                Uncertainties: ["EditionNeedsCatalogueVerification"]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(999, "tv", null, "eg1", "g1", null));
        _tmdb.GetDetailsAsync(999, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(999, "tv", "Example", "Example", 2020, 1,
                null, null, null, null, null, "{}", [new(1, 50)]));
        _tmdb.GetEpisodeGroupAsync("eg1", Arg.Any<CancellationToken>())
            .Returns(new TmdbEpisodeGroup("eg1", "HD Remaster", 6,
                [new("g1", "HD Remaster", 1, [new(0, 1, 5, "Fifth", 1005), new(1, 1, 9, "Ninth", 1009)])]));
        (await Run()).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().TargetPath.Should().BeNull();
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Example", "Example")]
    [InlineData("Example", "Example!")]
    [InlineData("Example", "Example+")]
    [InlineData("A+B", "AB")]
    [InlineData("Numbered Story 2.22", "Numbered Story 222")]
    [InlineData("Café", "Cafe\u0301")]
    [InlineData("Example Show", "Example  Show")]
    public async Task UnverifiedTitleCannotEscapeThroughAiRematch(string pendingTitle, string returnedTitle)
    {
        string primary = pendingTitle + " Unverified Arc";
        RuleParseResult rule = new(primary, null, "tv", 1, 3, null, 0.5, false, null,
            AlternativeTitles: [pendingTitle], NamingEvidence: new(primary, [],
                TitleCandidateDecisions: [new(pendingTitle, "Candidate", "ArcBaseTitleNeedsCatalogue")]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCallOutcome(true, new AiParseResult(returnedTitle, null, "tv", 1, 3, null, 0.95), 1L, 1, null));
        TmdbCandidate candidate = new(999, "tv", returnedTitle, returnedTitle, 2020, 999999, "en", null, null, null);
        int lookups = 0;
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new TmdbSearchResult(++lookups == 1 ? [] : [candidate], null));
        ProcessFileOutcome result = await NewSut().ProcessAsync(new PendingFileItem(
            Path.Combine(Path.GetTempPath(), primary + " S01E03.mkv"), 1, PendingFileSource.Watcher), CancellationToken.None);
        ReadOne().TmdbId.Should().BeNull();
        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        await _tmdb.Received().SearchAsync(Arg.Is<TmdbSearchRequest>(request => request.Query == returnedTitle),
            Arg.Any<CancellationToken>());
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        using PmmDbContext database = _dbFactory.CreateDbContext();
        database.ProcessSteps.Select(step => step.Detail).ToArray().Should()
            .Contain(detail => detail != null && detail.Contains("UnverifiedTitleCandidate"));
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdOnlyManualMarkerDoesNotProveTvEditionEpisodeMapping()
    {
        RuleParseResult rule = new("Example HD Remaster", null, "tv", 1, 3, null, 0.99, false, null,
            NamingEvidence: new("Example HD Remaster", ["HD Remaster"],
                Uncertainties: ["EditionNeedsCatalogueVerification"]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(999, "tv", 1, null, null, null));
        _tmdb.GetDetailsAsync(999, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(999, "tv", "Example", "Example", 2020, 1,
                null, null, null, null, null, "{}", [new(1, 50)]));
        ProcessFileOutcome result = await Run();
        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().TargetPath.Should().BeNull();
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TvRemasterCannotAcquireIdentityFromCatalogueRangeAlone()
    {
        RuleParseResult rule = new("Example HD Remaster", null, "tv", 1, 3, null, 0.99, false, null,
            NamingEvidence: new("Example HD Remaster", ["HD Remaster"],
                Uncertainties: ["EditionNeedsCatalogueVerification"]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        TmdbCandidate candidate = new(999, "tv", "Example HD Remaster", "Example HD Remaster", 2020, 999999, "en", null, null, null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([candidate], null));
        ProcessFileOutcome result = await NewSut().ProcessAsync(new PendingFileItem(
            Path.Combine(Path.GetTempPath(), "Example HD Remaster S01E03.mkv"), 1, PendingFileSource.Watcher), CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem saved = ReadOne();
        saved.TmdbId.Should().BeNull(); saved.TargetPath.Should().BeNull();
        ParsedInfo info = ParsedInfo.FromJson(saved.ParsedInfo)!;
        info.Season.Should().Be(1); info.Episode.Should().Be(3);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RuleNumberingKind.AirDate)]
    [InlineData(RuleNumberingKind.Volume)]
    [InlineData(RuleNumberingKind.Absolute)]
    [InlineData(RuleNumberingKind.Cour)]
    public async Task UnmappedNumberCannotAcquireIdentityFromOneTmdbCandidate(RuleNumberingKind kind)
    {
        RuleParseResult rule = new("Example", null, "tv", null, null, null, 0.99, false, null,
            NumberingEvidence: [new(kind, RuleEvidenceState.Candidate, "sourceNumber", "FileName", null, 0, 1, "2", 2)]);
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        TmdbCandidate candidate = new(999, "tv", "Example", "Example", 2020, 999999, "en", null, null, null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([candidate], null));
        ProcessFileOutcome result = await NewSut().ProcessAsync(new PendingFileItem(
            Path.Combine(Path.GetTempPath(), "Example.mkv"), 1, PendingFileSource.Watcher), CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem saved = ReadOne();
        saved.TmdbId.Should().BeNull(); saved.TargetPath.Should().BeNull();
        ParsedInfo info = ParsedInfo.FromJson(saved.ParsedInfo)!;
        info.Season.Should().BeNull(); info.Episode.Should().BeNull();
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ArcBaseTitleNeedsCatalogue")]
    [InlineData("EditionNeedsCatalogue")]
    public async Task UnverifiedRuleQueryCannotBindEvenOneExactPopularCandidate(string reason)
    {
        const string primary = "Example Full Title";
        const string query = "Example";
        RuleParseResult rule = new(primary, null, "unknown", null, null, null, 0.5, false, null,
            AlternativeTitles: [query], NamingEvidence: new(primary, [],
                TitleCandidateDecisions: [new(query, "Candidate", reason)]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        TmdbCandidate candidate = new(999, "movie", query, query, 2020, 999999, "en", null, null, null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new TmdbSearchResult(call.Arg<TmdbSearchRequest>().Query == query ? [candidate] : [], null));
        ProcessFileOutcome result = await NewSut().ProcessAsync(new PendingFileItem(
            Path.Combine(Path.GetTempPath(), primary + ".mkv"), 1, PendingFileSource.Watcher), CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem saved = ReadOne();
        saved.TmdbId.Should().BeNull(); saved.TargetPath.Should().BeNull();
        ParsedInfo.FromJson(saved.ParsedInfo)!.Title.Should().Be(primary);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        await _classify.DidNotReceive().ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactDecimalSourceQueryPreservesPrimaryCandidatesAndDistinctTypedIds()
    {
        const string raw = "Numbered Story 2.22 A New Chapter";
        string cleaned = raw.Replace('.', ' ');
        RuleTitleVariant variant = new(cleaned, "Latin", null, "FileName", null, 0, raw.Length, raw, null);
        RuleParseResult rule = new(cleaned, null, "unknown", null, null, null, 0.9, false, null,
            AlternativeTitles: [raw], NamingEvidence: new(cleaned, [], TitleVariants: [variant]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        TmdbCandidate[] original = Enumerable.Range(1, 20)
            .Select(id => new TmdbCandidate(id, "movie", cleaned, null, null, 0, null, null, null, null)).ToArray();
        TmdbCandidate newType = new(1, "tv", raw, null, null, 99999, null, null, null, null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new TmdbSearchResult(call.Arg<TmdbSearchRequest>().Query == raw
                ? [newType, original[0]] : original, null));

        ProcessFileOutcome result = await NewSut().ProcessAsync(new PendingFileItem(
            Path.Combine(Path.GetTempPath(), raw + ".mkv"), 1, PendingFileSource.Watcher), CancellationToken.None);

        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem saved = ReadOne();
        saved.TmdbId.Should().BeNull();
        using JsonDocument candidates = JsonDocument.Parse(saved.TmdbCandidatesJson!);
        candidates.RootElement.GetArrayLength().Should().Be(21);
        candidates.RootElement.EnumerateArray().Should().Contain(c => c.GetProperty("tmdbId").GetInt32() == 1
            && c.GetProperty("mediaType").GetString() == "movie");
        candidates.RootElement.EnumerateArray().Should().Contain(c => c.GetProperty("tmdbId").GetInt32() == 1
            && c.GetProperty("mediaType").GetString() == "tv");
        await _classify.DidNotReceive().ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactDecimalSourceQueryCannotAutoBindEvenOnePopularCandidate()
    {
        const string raw = "Numbered Story 2.22 A New Chapter";
        string cleaned = raw.Replace('.', ' ');
        RuleTitleVariant variant = new(cleaned, "Latin", null, "FileName", null, 0, raw.Length, raw, null);
        RuleParseResult rule = new(cleaned, null, "unknown", null, null, null, 0.5, false, null,
            AlternativeTitles: [raw], NamingEvidence: new(cleaned, [], TitleVariants: [variant]));
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>()).Returns(rule);
        TmdbCandidate candidate = new(999, "movie", raw, raw, 2020, 999999, "en", null, null, null);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new TmdbSearchResult(call.Arg<TmdbSearchRequest>().Query == raw ? [candidate] : [], null));
        ProcessFileOutcome result = await NewSut().ProcessAsync(new PendingFileItem(
            Path.Combine(Path.GetTempPath(), raw + ".mkv"), 1, PendingFileSource.Watcher), CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem saved = ReadOne();
        saved.TmdbId.Should().BeNull(); saved.TargetPath.Should().BeNull();
        ParsedInfo.FromJson(saved.ParsedInfo)!.Title.Should().Be(cleaned);
        await _aiOrchestrator.DidNotReceive().ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _classify.DidNotReceive().ClassifyAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }
}
