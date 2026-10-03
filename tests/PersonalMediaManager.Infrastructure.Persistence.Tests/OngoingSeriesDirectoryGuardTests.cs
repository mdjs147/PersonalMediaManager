using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>有效整剧状态证据与未知兼容规则</summary>
public sealed class OngoingSeriesDirectoryGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("{\"status\":\"Returning Series\",\"in_production\":true}", true)]
    [InlineData("{\"status\":\"Returning Series\"}", true)]
    [InlineData("{\"status\":\"In Production\"}", true)]
    [InlineData("{\"in_production\":true}", true)]
    [InlineData("{\"status\":\"Ended\",\"in_production\":false}", false)]
    [InlineData("{\"status\":\"Canceled\"}", false)]
    [InlineData("{\"status\":\"Ended\",\"in_production\":true}", false)]
    [InlineData("{\"status\":\"Returning Series\",\"in_production\":false}", false)]
    [InlineData("{\"status\":\"Unknown\"}", false)]
    [InlineData("{\"status\":\"Unknown\",\"in_production\":true}", false)]
    [InlineData("{\"in_production\":\"true\"}", false)]
    [InlineData("{\"status\":5,\"in_production\":true}", false)]
    [InlineData("{\"status\":\"Returning Series\",\"in_production\":\"true\"}", false)]
    [InlineData("{\"number_of_episodes\":12,\"number_of_seasons\":1}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("broken", false)]
    [InlineData("{\"id\":999,\"status\":\"Returning Series\"}", false)]
    [InlineData("{\"id\":\"1\",\"status\":\"Returning Series\"}", false)]
    [InlineData("{\"id\":1,\"status\":\"Returning Series\"}", true)]
    public void OnlyConfirmedOngoingEvidenceIsAccepted(string raw, bool expected)
    {
        OngoingSeriesDirectoryGuard.IsConfirmedOngoing(Cache(raw), Now, 24).Should().Be(expected);
    }

    [Theory]
    [InlineData("tv", -25, 24)]
    [InlineData("tv", 1, 24)]
    [InlineData("tv", 0, 0)]
    [InlineData("tv", 0, -1)]
    [InlineData("movie", 0, 24)]
    public void StaleFutureDisabledOrMovieCacheIsNotOngoing(string type, int offsetHours, int ttlHours)
    {
        TmdbMetadataCache cache = Cache("{\"status\":\"Returning Series\"}");
        cache.MediaType = type;
        cache.CachedAt = Now.AddHours(offsetHours);
        OngoingSeriesDirectoryGuard.IsConfirmedOngoing(cache, Now, ttlHours).Should().BeFalse();
    }

    [Fact]
    public void MissingCacheAndMissingTimestampAreUnknown()
    {
        OngoingSeriesDirectoryGuard.IsConfirmedOngoing(null, Now, 24).Should().BeFalse();
        TmdbMetadataCache cache = Cache("{\"status\":\"Returning Series\"}");
        cache.CachedAt = default;
        OngoingSeriesDirectoryGuard.IsConfirmedOngoing(cache, Now, 24).Should().BeFalse();
    }

    private static TmdbMetadataCache Cache(string raw) => new()
    {
        TmdbId = 1, MediaType = "tv", RawJson = raw, CachedAt = Now,
    };
}
