using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Review;

namespace PersonalMediaManager.Application.Tests;

public sealed class ReviewEpisodeMappingGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private static TmdbDetailsResult Details(string type = "tv", int id = 101) => new(id, type, "合成连续剧", null,
        2020, 2, null, null, null, null, null, "{}", [new(0, 3), new(1, 10), new(2, 8)], CachedAt: Now);
    private static TmdbSeasonCatalogueResult Season(int season, int count, int id = 101) => new(id, "tv",
        new(season, null, null, null, null, Enumerable.Range(1, count).Select(e => new TmdbEpisodeRef(e, $"第{e}集",
            null, null, Now.AddYears(-1), null, null)).ToArray()), Now);
    private static ReviewEpisodeMappingGuard.Result Map(TmdbDetailsResult? details = null,
        IReadOnlyList<TmdbSeasonCatalogueResult>? seasons = null, int episode = 12, int? end = null) =>
        ReviewEpisodeMappingGuard.Map(101, 2, episode, end, details ?? Details(), seasons ?? [Season(1, 10), Season(2, 8)], Now);

    [Fact]
    public void CompleteSameTvCatalogues_MapAbsoluteRange_ExcludeSpecials()
    {
        ReviewEpisodeMappingGuard.Result result = Map(episode: 12, end: 14);
        result.Error.Should().BeNull(); result.Episode.Should().Be(2); result.EpisodeEnd.Should().Be(4);
        result.Offset.Should().Be(10); result.Evidence.Should().Contain(e => e.Contains("逐集条目"));
        result.CatalogueFingerprint.Should().NotBeNullOrEmpty();
    }
    [Theory]
    [InlineData("movie", 101)] [InlineData("tv", 102)]
    public void SameNumericMovieId_OrDifferentTvId_AreRejected(string type, int id) => Map(Details(type, id)).Error.Should().NotBeNull();
    [Fact]
    public void SummaryCountAlone_IsNotProof() => Map(seasons: []).Error.Should().NotBeNull();
    [Fact]
    public void MissingEarlierSeason_AndSparseOrDuplicateEpisodes_BlockMapping()
    {
        Map(seasons: [Season(2, 8)]).Error.Should().NotBeNull();
        TmdbSeasonCatalogueResult prior = Season(1, 10);
        TmdbSeasonCatalogueResult sparse = prior with { Season = prior.Season! with { Episodes = prior.Season.Episodes.Skip(1).ToArray() } };
        Map(seasons: [sparse, Season(2, 8)]).Error.Should().NotBeNull();
        TmdbSeasonCatalogueResult duplicate = prior with { Season = prior.Season! with { Episodes = prior.Season.Episodes.Select(e => e with { EpisodeNumber = 1 }).ToArray() } };
        Map(seasons: [duplicate, Season(2, 8)]).Error.Should().NotBeNull();
    }
    [Fact]
    public void StaleFailureOrUnknownTime_CannotConfirmMapping()
    {
        Map(Details() with { RefreshError = "限流" }).Error.Should().NotBeNull();
        Map(seasons: [Season(1, 10) with { RefreshError = "断网" }, Season(2, 8)]).Error.Should().NotBeNull();
        Map(seasons: [Season(1, 10) with { CachedAt = null }, Season(2, 8)]).Error.Should().NotBeNull();
    }
    [Fact]
    public void UnairedPriorEpisodes_CannotSetAbsoluteOffset()
    {
        TmdbSeasonCatalogueResult prior = Season(1, 10);
        TmdbSeasonCatalogueResult future = prior with { Season = prior.Season! with { Episodes = prior.Season.Episodes.Select(e => e with { AirDate = Now.AddDays(1) }).ToArray() } };
        Map(seasons: [future, Season(2, 8)]).Error.Should().NotBeNull();
    }
    [Theory]
    [InlineData(10, null)] [InlineData(19, null)] [InlineData(12, 19)] [InlineData(12, 11)]
    public void OutsideTargetSeason_OrCrossSeasonRange_IsRejected(int episode, int? end) => Map(episode: episode, end: end).Error.Should().NotBeNull();
}
