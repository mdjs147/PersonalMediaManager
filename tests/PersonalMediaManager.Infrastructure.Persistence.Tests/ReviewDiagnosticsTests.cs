using PersonalMediaManager.Application.Contracts;
using System.Text.Json;
using NSubstitute;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ReviewServiceTests
{
    private sealed class ReviewDiagnosticSink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }

    [Fact]
    public async Task Diagnostics_ManualConfirmationStoresOldAndNewWithoutGroundTruthClaim()
    {
        long category = SeedCategory();
        long id = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule, 111, "movie");
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception.mkv", ArchiveOutcome.Completed));
        ReviewDiagnosticSink sink = new();
        using (ParseDiagnostics.Begin("test", sink: sink))
            await _sut.ConfirmAsync(id, new ConfirmRequest(27205, "movie", category, "Inception", 2010, null, null, ReadItem(id).RowVersion));
        ParseDiagnosticEvent committed = sink.Events.Single(e => e.Name == "manual.change_committed");
        committed.MediaItemId.Should().Be(id);
        committed.Data.GetProperty("groundTruth").GetBoolean().Should().BeFalse();
        JsonElement evidence = committed.Data.GetProperty("evidence");
        evidence.GetProperty("before").GetProperty("tmdbId").GetInt32().Should().Be(111);
        evidence.GetProperty("after").GetProperty("tmdbId").GetInt32().Should().Be(27205);
        evidence.GetProperty("after").GetProperty("title").GetString().Should().Be("Inception");
    }
}
