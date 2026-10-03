using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Infrastructure.Persistence.Services.Library;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>库内富化刷新保留可用简介与其他季，不影响正典目录</summary>
public sealed class WorkSeasonMetadataRefreshTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContextFactory _factory;
    private readonly ITmdbClient _client = Substitute.For<ITmdbClient>();
    private readonly ITmdbSearchService _catalogue = Substitute.For<ITmdbSearchService>();
    private readonly WorkEnrichmentService _sut;

    public WorkSeasonMetadataRefreshTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.TmdbSettings.Single().ApiKeyEncrypted = "测试值";
            MediaWork work = MediaWork.CreateMinimal(42, "tv", "原作品", 2020);
            db.MediaWorks.Add(work);
            db.SaveChanges();
            work.UpsertScalars("原作品", null, 2020, "作品原简介", null, null, null, null, null, null,
                null, null, null, null, null, 2, 3);
            work.ReplaceSeasons([new(1, "第一季", "季原简介", "/s.jpg", null, 2), new(2, "第二季", "另一季", null, null, 1)]);
            work.ReplaceSeasonEpisodes(1, [new(1, 1, "第一集", "集原简介", "/e.jpg", null, 30, 8), new(1, 2, "第二集", "第二集原简介", null, null, null, null)]);
            work.ReplaceSeasonEpisodes(2, [new(2, 1, "另一季第一集", "另一季集简介", null, null, null, null)]);
            db.SaveChanges();
        }
        IProtectedFieldService protector = Substitute.For<IProtectedFieldService>();
        protector.Unprotect(Arg.Any<string>()).Returns("测试值");
        _sut = new WorkEnrichmentService(_factory, protector, _client, Substitute.For<IPosterDownloader>(),
            AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), "pmm-work-season-tests")),
            new WorkEnrichmentBackoff(), NullLogger<WorkEnrichmentService>.Instance, _catalogue);
        _client.GetEnrichedDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Details());
        SetCatalogue(new(42, "tv", new(1, "第一季", null, null, null, [new(1, "新集名", null, null, null, null, null)]), DateTimeOffset.UtcNow));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ForcedWorkRefreshPreservesEpisodesAndMissingOverview()
    {
        await _sut.EnrichAsync(42, "tv", true);
        using PmmDbContext db = _factory.CreateDbContext();
        MediaWork work = db.MediaWorks.Include(w => w.Seasons).Include(w => w.Episodes).Single();
        work.Title.Should().Be("新作品名");
        work.Overview.Should().Be("作品原简介");
        work.Episodes.Should().HaveCount(3);
        work.Seasons.Single(s => s.SeasonNumber == 1).Overview.Should().Be("季原简介");
        work.Seasons.Single(s => s.SeasonNumber == 2).Overview.Should().Be("另一季");
    }

    [Theory]
    [InlineData(43, "tv", "{}")]
    [InlineData(42, "movie", "{}")]
    [InlineData(42, "tv", "{\"id\":43}")]
    public async Task InvalidEnrichedIdentityDoesNotChangeWorkOrEpisodes(int id, string type, string raw)
    {
        _client.GetEnrichedDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Details() with { TmdbId = id, MediaType = type, RawJson = raw });
        await _sut.Invoking(s => s.EnrichAsync(42, "tv", true)).Should().ThrowAsync<TmdbClientException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaWorks.Single().Title.Should().Be("原作品");
        db.MediaWorks.Single().Overview.Should().Be("作品原简介");
        db.MediaEpisodes.Count().Should().Be(3);
    }

    [Fact]
    public async Task ExistingSeasonStillConsultsCatalogueTtlOnEveryRead()
    {
        await _sut.EnsureSeasonEpisodesAsync(42, "tv", 1, false);
        await _sut.EnsureSeasonEpisodesAsync(42, "tv", 1, false);
        await _catalogue.Received(2).GetSeasonCatalogueAsync(42, 1, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeasonRefreshMergesMissingFieldsAndEpisodesWithoutChangingOtherSeason()
    {
        await _sut.EnsureSeasonEpisodesAsync(42, "tv", 1, true);
        await _catalogue.Received(1).GetSeasonCatalogueAsync(42, 1, true, Arg.Any<CancellationToken>());
        using PmmDbContext db = _factory.CreateDbContext();
        List<MediaEpisode> episodes = db.MediaEpisodes.ToList();
        episodes.Should().HaveCount(3);
        MediaEpisode first = episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1);
        first.Name.Should().Be("新集名");
        first.Overview.Should().Be("集原简介");
        first.StillPath.Should().Be("/e.jpg");
        first.Runtime.Should().Be(30);
        episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 2).Overview.Should().Be("第二集原简介");
        episodes.Single(e => e.SeasonNumber == 2).Overview.Should().Be("另一季集简介");
    }

    [Fact]
    public async Task WorkRefreshThenFailedSeasonRefreshDoesNotLoseExistingEpisodes()
    {
        await _sut.EnrichAsync(42, "tv", true);
        SetCatalogue(new(42, "tv", null, RefreshError: "网络不可达"));
        await _sut.Invoking(s => s.EnsureSeasonEpisodesAsync(42, "tv", 1, true)).Should().ThrowAsync<TmdbClientException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaEpisodes.Count().Should().Be(3);
        db.MediaEpisodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1).Overview.Should().Be("集原简介");
    }

    [Fact]
    public async Task StaleResultDoesNotOverwriteLibraryAndMovieDoesNotRequestSeason()
    {
        SetCatalogue(new(42, "tv", new(1, null, null, null, null, [new(1, "旧目录名", "旧目录简介", null, null, null, null)]),
            DateTimeOffset.UtcNow.AddDays(-2), true, "TMDB 429"));
        await _sut.Invoking(s => s.EnsureSeasonEpisodesAsync(42, "tv", 1, true)).Should().ThrowAsync<TmdbClientException>();
        await _sut.EnsureSeasonEpisodesAsync(42, "movie", 1, true);
        await _catalogue.Received(1).GetSeasonCatalogueAsync(42, 1, true, Arg.Any<CancellationToken>());
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaEpisodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1).Name.Should().Be("第一集");
    }

    [Theory]
    [InlineData(43, "tv", 1)]
    [InlineData(42, "movie", 1)]
    [InlineData(42, "tv", 2)]
    public async Task WrongCatalogueIdentityNeverWritesLibrary(int id, string type, int season)
    {
        SetCatalogue(new(id, type, new(season, null, null, null, null, []), DateTimeOffset.UtcNow));
        await _sut.Invoking(s => s.EnsureSeasonEpisodesAsync(42, "tv", 1, true)).Should().ThrowAsync<TmdbClientException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaEpisodes.Count().Should().Be(3);
    }

    [Fact]
    public async Task FreshSharedCatalogueBypassesEarlierLibraryFailureBackoff()
    {
        SetCatalogue(new(42, "tv", null, RefreshError: "网络不可达"));
        await _sut.Invoking(s => s.EnsureSeasonEpisodesAsync(42, "tv", 1, true)).Should().ThrowAsync<TmdbClientException>();
        SetCatalogue(new(42, "tv", new(1, null, null, null, null,
            [new(1, "刚刷新成功的集名", "新简介", null, null, null, null)]), DateTimeOffset.UtcNow, true));
        await _sut.EnsureSeasonEpisodesAsync(42, "tv", 1, false);
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaEpisodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1).Name.Should().Be("刚刷新成功的集名");
    }

    [Fact]
    public async Task SeasonCancellationPropagatesAndPreservesLibrary()
    {
        _catalogue.GetSeasonCatalogueAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TmdbSeasonCatalogueResult>(new OperationCanceledException()));
        await _sut.Invoking(s => s.EnsureSeasonEpisodesAsync(42, "tv", 1, true)).Should().ThrowAsync<OperationCanceledException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaEpisodes.Count().Should().Be(3);
    }

    [Fact]
    public async Task EmptySuccessfulEnrichmentCannotEraseWorkOrEpisodes()
    {
        _client.GetEnrichedDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Details() with { Title = null, OriginalTitle = " ", RawJson = "{}" });
        await _sut.Invoking(s => s.EnrichAsync(42, "tv", true)).Should().ThrowAsync<TmdbClientException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaWorks.Single().Title.Should().Be("原作品");
        db.MediaWorks.Single().Overview.Should().Be("作品原简介");
        db.MediaEpisodes.Count().Should().Be(3);
    }

    private void SetCatalogue(TmdbSeasonCatalogueResult result)
        => _catalogue.GetSeasonCatalogueAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(result);
    private static TmdbEnrichedDetails Details() => new(42, "tv", "新作品名", null, 2020, null, null, null, null, null,
        null, null, null, null, null, null, null, 2, 3, [], [], [], [], [], [], [new(1, "第一季", "", null, null, 2)], "{}");
    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        public PmmDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection).Options);
    }
}
