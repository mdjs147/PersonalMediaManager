using System.Text.Json;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ProcessFileServiceTests
{
    [Fact]
    public async Task AiTaskRetainsNamingHintsAndExactRuleTokensWithoutChangingDefaultSchema()
    {
        const string name = "EXAMPLE New Arc HD Remaster S03E01.mkv";
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Example", null, "tv", 3, 1, null, .3, false, 1,
                SeasonTitle: "New Arc", FieldEvidence: [new("season", 3, "FileName", "S03")],
                NamingEvidence: new("EXAMPLE New Arc HD Remaster", ["HD Remaster"])));
        ConfigureAi(false);

        (await NewSut().ProcessAsync(new PendingFileItem(Path.Combine(Path.GetTempPath(), name), 0, PendingFileSource.Manual),
            CancellationToken.None)).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        await _aiOrchestrator.Received(1).ExecuteAsync(Arg.Is<AiParseRequest>(q => q.Context != null
            && q.Context.SchemaVersion == 1 && q.Context.SeasonTitle == "New Arc"
            && q.Context.EditionTags != null && q.Context.EditionTags.Contains("HD Remaster")
            && q.Context.RuleProvenance != null && q.Context.RuleProvenance.Any(e => e.Field == "season" && e.Token == "S03")
            && q.Context.TextEvidence != null && q.Context.TextEvidence.Any(e => e.Field == "title" && e.Value == "Example" && e.Token == "EXAMPLE")),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing", "CatalogueUnknown")]
    [InlineData("empty", "CatalogueUnknown")]
    [InlineData("zeroCount", "EpisodeCountUnknown")]
    [InlineData("outside", "EpisodeOutsideCatalogue")]
    [InlineData("onlySpecials", "SingleSeasonCatalogueMismatch")]
    [InlineData("onlySecond", "FirstSeasonAbsent")]
    [InlineData("differentId", "CatalogueIdentityMismatch")]
    [InlineData("differentType", "CatalogueIdentityMismatch")]
    [InlineData("duplicate", "SeasonCatalogueConflict")]
    public async Task LegacyEpisodeWithoutNewFieldFlags_StillRequiresCatalogueForS1(string variant, string reason)
    {
        ConfigureRule(.9, false, year: null, title: "Example", episode: 3, mediaType: "tv");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "tv", "Example")], null));
        IReadOnlyList<TmdbSeasonInfo>? seasons = variant switch
        {
            "missing" => null,
            "empty" => [],
            "zeroCount" => [new(1, 0)],
            "outside" => [new(1, 2)],
            "onlySpecials" => [new(0, 12)],
            "onlySecond" => [new(2, 12)],
            "duplicate" => [new(1, 12), new(1, 12)],
            _ => [new(1, 12)],
        };
        _tmdb.GetDetailsAsync(42, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(variant == "differentId" ? 43 : 42, variant == "differentType" ? "movie" : "tv",
                "Example", "Example", 2024, 1, null, null, null, null, null, "{}", seasons));

        (await Run()).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ParsedInfo.FromJson(ReadOne().ParsedInfo)!.Season.Should().BeNull();
        JsonElement audit = ReadTmdbReconciliation();
        audit.GetProperty("seasonInference").GetProperty("reasonCode").GetString().Should().Be(reason);
        audit.GetProperty("preTmdb").GetProperty("season").ValueKind.Should().Be(JsonValueKind.Null);
        audit.GetProperty("postTmdb").GetProperty("season").ValueKind.Should().Be(JsonValueKind.Null);
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleSeasonInferencePersistsBeforeAfterAndCatalogueEvidence()
    {
        ConfigureRule(.9, false, year: null, title: "Example", episode: 3, mediaType: "tv");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "tv", "Example")], null));
        _tmdb.GetDetailsAsync(42, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(42, "tv", "Example", "Example", 2024, 1, null, null, null, null, null, "{}",
                [new(0, 3), new(1, 12)], FromCache: true));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/Example/S01E03.mkv");

        (await Run()).Outcome.Should().Be(ProcessOutcome.Completed);
        JsonElement audit = ReadTmdbReconciliation();
        audit.GetProperty("preTmdb").GetProperty("season").ValueKind.Should().Be(JsonValueKind.Null);
        audit.GetProperty("postTmdb").GetProperty("season").GetInt32().Should().Be(1);
        audit.GetProperty("seasonInference").GetProperty("applied").GetBoolean().Should().BeTrue();
        audit.GetProperty("catalogue").GetProperty("status").GetString().Should().Be("verified");
        audit.GetProperty("catalogue").GetProperty("source").GetString().Should().Be("cache");
        await _tmdb.Received(1).GetDetailsAsync(42, "tv", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("edition")]
    [InlineData("seasonTitle")]
    [InlineData("seasonCandidate")]
    [InlineData("rejectedSeason")]
    public async Task EditionAndUnresolvedSeasonContextPreventDefaultS1(string kind)
    {
        _ruleEngine.ParseAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new RuleParseResult("Example", null, "tv", null, 3, null, .9, false, 1,
                SeasonTitle: kind == "seasonTitle" ? "New Arc" : null,
                RejectedFields: kind == "rejectedSeason" ? ["season"] : null,
                NamingEvidence: new("Example", kind == "edition" ? ["HD Remaster"] : [],
                    SeasonCandidate: kind == "seasonCandidate" ? 3 : null)));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "tv", "Example")], null));
        _tmdb.GetDetailsAsync(42, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(42, "tv", "Example", "Example", 2024, 1, null, null, null, null, null, "{}", [new(1, 12)]));

        (await Run()).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ParsedInfo.FromJson(ReadOne().ParsedInfo)!.Season.Should().BeNull();
        ReadOne().TmdbId.Should().BeNull("待核验编号或版本在身份写入之前就应转审核");
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ProcessSteps.Select(step => step.Detail).ToArray().Should()
            .Contain(detail => detail != null && detail.Contains("unresolvedFields"));
        await _tmdb.DidNotReceive().GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unknown", "CandidateTypeResolved")]
    [InlineData("tv", "InferredTypeCorrected")]
    public async Task VerifiedMovieTypeReplacesWeakInferenceButPreservesBeforeAfter(string extractedType, string reason)
    {
        ConfigureRule(.9, false, year: null, title: "Example", mediaType: extractedType);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "movie", "Example")], null));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Movies/Example.mkv");

        (await Run()).Outcome.Should().Be(ProcessOutcome.Completed);
        MediaItem media = ReadOne();
        media.TmdbMediaType.Should().Be("movie");
        ParsedInfo.FromJson(media.ParsedInfo)!.Type.Should().Be("movie");
        JsonElement audit = ReadTmdbReconciliation();
        audit.GetProperty("preTmdb").GetProperty("type").GetString().Should().Be(extractedType);
        audit.GetProperty("postTmdb").GetProperty("type").GetString().Should().Be("movie");
        audit.GetProperty("typeDecision").GetProperty("reasonCode").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task ExplicitEpisodeMovieConflictKeepsSourceFieldsAndDoesNotBindOrArchive()
    {
        ConfigureRule(.9, false, year: null, title: "Example", season: 2, episode: 3, mediaType: "tv");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(42, "movie", "Example")], null));

        (await Run()).Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        ReadOne().TmdbId.Should().BeNull();
        JsonElement audit = ReadTmdbReconciliation();
        audit.GetProperty("status").GetString().Should().Be("conflict");
        audit.GetProperty("preTmdb").GetProperty("season").GetInt32().Should().Be(2);
        audit.GetProperty("postTmdb").GetProperty("episode").GetInt32().Should().Be(3);
        audit.GetProperty("postTmdb").GetProperty("type").GetString().Should().Be("tv");
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualIdentityOverrideKeepsActualPreTmdbExtractionInAudit()
    {
        ConfigureRule(.2, false, year: null, title: "Source Title", episode: 3, mediaType: "unknown");
        _forcedMatch.TryReadAsync(Arg.Any<FileParseContext>(), Arg.Any<CancellationToken>())
            .Returns(new ForcedMatchMarker(42, "tv", Season: 2, EpisodeGroupId: null, GroupId: null, TitleOverride: null));
        _tmdb.GetDetailsAsync(42, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(42, "tv", "Canonical Title", "Canonical Title", 2024, 2,
                null, null, null, null, null, "{}"));
        ConfigureClassify(ClassifyDecision.Matched, 1);
        ConfigureArchive(ArchiveOutcome.Completed, "/Tv/Example/S02E03.mkv");

        (await Run()).Outcome.Should().Be(ProcessOutcome.Completed);
        JsonElement audit = ReadTmdbReconciliation();
        audit.GetProperty("preTmdb").GetProperty("title").GetString().Should().Be("Source Title");
        audit.GetProperty("preTmdb").GetProperty("type").GetString().Should().Be("unknown");
        audit.GetProperty("preTmdb").GetProperty("season").ValueKind.Should().Be(JsonValueKind.Null);
        audit.GetProperty("postTmdb").GetProperty("title").GetString().Should().Be("Canonical Title");
        audit.GetProperty("postTmdb").GetProperty("season").GetInt32().Should().Be(2);
        audit.GetProperty("typeDecision").GetProperty("reasonCode").GetString().Should().Be("ManualIdentityType");
    }

    private JsonElement ReadTmdbReconciliation()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string detail = db.ProcessSteps.OrderBy(s => s.Id).Select(s => s.Detail).AsEnumerable()
            .Last(s => s?.Contains("\"tmdbReconciliation\"", StringComparison.Ordinal) == true)!;
        using JsonDocument doc = JsonDocument.Parse(detail);
        return doc.RootElement.GetProperty("tmdbReconciliation").Clone();
    }
}
