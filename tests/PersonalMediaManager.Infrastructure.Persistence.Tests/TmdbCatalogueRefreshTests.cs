using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>详情与单季目录的强刷、TTL、身份隔离及失败保全</summary>
public sealed class TmdbCatalogueRefreshTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContextFactory _factory;
    private readonly ITmdbClient _client = Substitute.For<ITmdbClient>();
    private readonly TmdbSearchService _sut;
    private readonly MetadataLogCapture<TmdbSearchService> _logger = new();

    public TmdbCatalogueRefreshTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            TmdbSetting setting = db.TmdbSettings.Single();
            setting.ApiKeyEncrypted = "已加密测试值";
            setting.RateLimitPerSecond = 7;
            db.SaveChanges();
        }
        IProtectedFieldService protector = Substitute.For<IProtectedFieldService>();
        protector.Unprotect(Arg.Any<string>()).Returns("测试值");
        _sut = new TmdbSearchService(_factory, protector, _client, Substitute.For<IPosterDownloader>(),
            AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), "pmm-catalogue-tests")), _logger);
        StubDetails(Details());
        StubSeason(Season());
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Details_FreshCacheUsesTimestamp_ForceAndExpiryFetchRemote()
    {
        TmdbDetailsResult first = await _sut.GetDetailsFreshAsync(42, "tv");
        TmdbDetailsResult cached = await _sut.GetDetailsFreshAsync(42, "TV");
        cached.FromCache.Should().BeTrue();
        cached.CachedAt.Should().Be(first.CachedAt);
        cached.RefreshError.Should().BeNull();
        StubDetails(Details("新标题"));
        TmdbDetailsResult forced = await _sut.GetDetailsFreshAsync(42, "tv", true);
        forced.FromCache.Should().BeFalse();
        forced.Title.Should().Be("新标题");
        ExpireCaches();
        await _sut.GetDetailsFreshAsync(42, "tv");
        await _client.Received(3).GetDetailsAsync(42, "tv", "测试值", "zh-CN", 7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Details_FailedRefreshKeepsOldDataAndTimestamp(int status)
    {
        TmdbDetailsResult first = await _sut.GetDetailsFreshAsync(42, "tv");
        StubDetailsFailure(new TmdbClientException("刷新失败", status));
        TmdbDetailsResult result = await _sut.GetDetailsFreshAsync(42, "tv", true);
        result.FromCache.Should().BeTrue();
        result.CachedAt.Should().Be(first.CachedAt);
        result.Title.Should().Be(first.Title);
        result.RefreshError.Should().Contain(status.ToString());
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Single().CachedAt.Should().Be(first.CachedAt);
        (await _sut.GetDetailsFreshAsync(42, "tv")).RefreshError.Should().BeNull();
    }

    [Fact]
    public async Task Details_OfflineWithoutCacheReportsError_LegacyApiStillThrows()
    {
        StubDetailsFailure(new HttpRequestException("网络不可达"));
        TmdbDetailsResult result = await _sut.GetDetailsFreshAsync(42, "tv", true);
        result.CachedAt.Should().BeNull();
        result.FromCache.Should().BeFalse();
        result.Seasons.Should().BeNull();
        result.RefreshError.Should().Be("TMDB 网络连接失败，请稍后重试");
        await _sut.Invoking(s => s.GetDetailsAsync(42, "tv")).Should().ThrowAsync<HttpRequestException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Should().BeEmpty();
    }

    [Fact]
    public async Task Details_SameNumberMovieAndTvNeverShareCache()
    {
        await _sut.GetDetailsFreshAsync(42, "tv");
        StubDetails(Details("同编号电影") with { MediaType = "movie", Seasons = null });
        (await _sut.GetDetailsFreshAsync(42, "movie")).Title.Should().Be("同编号电影");
        (await _sut.GetDetailsFreshAsync(42, "tv")).Title.Should().Be("原标题");
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Count().Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Details_InvalidFreshIdentityOrJsonPreservesOldCache(bool wrongIdentity)
    {
        TmdbDetailsResult first = await _sut.GetDetailsFreshAsync(42, "tv");
        StubDetails(wrongIdentity ? Details() with { MediaType = "movie" } : Details() with { RawJson = "损坏" });
        TmdbDetailsResult result = await _sut.GetDetailsFreshAsync(42, "tv", true);
        result.FromCache.Should().BeTrue();
        result.CachedAt.Should().Be(first.CachedAt);
        result.RefreshError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Details_CancellationAlwaysPropagatesAndPreservesCache()
    {
        TmdbDetailsResult first = await _sut.GetDetailsFreshAsync(42, "tv");
        StubDetailsFailure(new OperationCanceledException());
        await _sut.Invoking(s => s.GetDetailsFreshAsync(42, "tv", true)).Should().ThrowAsync<OperationCanceledException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Single().CachedAt.Should().Be(first.CachedAt);
    }

    [Fact]
    public async Task Season_UsesMetadataTtl_ForceRefreshAndRateSetting()
    {
        TmdbSeasonCatalogueResult first = await _sut.GetSeasonCatalogueAsync(42, 1);
        TmdbSeasonCatalogueResult cached = await _sut.GetSeasonCatalogueAsync(42, 1);
        cached.FromCache.Should().BeTrue();
        cached.CachedAt.Should().Be(first.CachedAt);
        StubSeason(Season(name: "更新季名"));
        TmdbSeasonCatalogueResult forced = await _sut.GetSeasonCatalogueAsync(42, 1, true);
        forced.FromCache.Should().BeFalse();
        forced.Season!.Name.Should().Be("更新季名");
        ExpireCaches();
        await _sut.GetSeasonCatalogueAsync(42, 1);
        await _client.Received(3).GetSeasonAsync(42, 1, "测试值", "zh-CN", 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Season_IdentitySeasonAndLanguageAreSeparateCacheKeys()
    {
        await _sut.GetSeasonCatalogueAsync(42, 1);
        StubSeason(Season(2));
        (await _sut.GetSeasonCatalogueAsync(42, 2)).FromCache.Should().BeFalse();
        StubSeason(Season());
        (await _sut.GetSeasonCatalogueAsync(43, 1)).FromCache.Should().BeFalse();
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.TmdbSettings.Single().Language = "en-US";
            db.SaveChanges();
        }
        (await _sut.GetSeasonCatalogueAsync(42, 1)).FromCache.Should().BeFalse();
        using PmmDbContext verify = _factory.CreateDbContext();
        verify.TmdbSearchCaches.Count().Should().Be(4);
        verify.TmdbSearchCaches.Select(c => c.QueryRaw).Should().OnlyContain(q => q!.StartsWith("season_catalogue:tv:"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Season_FailureReturnsExplicitStaleAndNeverChangesCache(int status)
    {
        await _sut.GetSeasonCatalogueAsync(42, 1);
        ExpireCaches();
        using PmmDbContext before = _factory.CreateDbContext();
        TmdbSearchCache original = before.TmdbSearchCaches.AsNoTracking().Single();
        StubSeasonFailure(new TmdbClientException("服务暂不可用", status));
        TmdbSeasonCatalogueResult result = await _sut.GetSeasonCatalogueAsync(42, 1);
        result.FromCache.Should().BeTrue();
        result.RefreshError.Should().Contain(status.ToString());
        result.CachedAt.Should().Be(original.CachedAt);
        result.Season!.Episodes.Should().ContainSingle();
        using PmmDbContext after = _factory.CreateDbContext();
        after.TmdbSearchCaches.Single().Results.Should().Be(original.Results);
        after.TmdbSearchCaches.Single().CachedAt.Should().Be(original.CachedAt);
    }

    [Fact]
    public async Task Season_OfflineWithoutCacheReturnsUnknown_NotInventedEpisodes()
    {
        StubSeasonFailure(new HttpRequestException("网络不可达"));
        TmdbSeasonCatalogueResult result = await _sut.GetSeasonCatalogueAsync(42, 1, true);
        result.Season.Should().BeNull();
        result.FromCache.Should().BeFalse();
        result.CachedAt.Should().BeNull();
        result.RefreshError.Should().Be("TMDB 网络连接失败，请稍后重试");
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbSearchCaches.Should().BeEmpty();
    }

    [Fact]
    public async Task Season_CancellationAndPrecancelledCacheHitAlwaysThrow()
    {
        TmdbSeasonCatalogueResult original = await _sut.GetSeasonCatalogueAsync(42, 1);
        StubSeasonFailure(new OperationCanceledException());
        await _sut.Invoking(s => s.GetSeasonCatalogueAsync(42, 1, true)).Should().ThrowAsync<OperationCanceledException>();
        await _sut.Invoking(s => s.GetSeasonCatalogueAsync(42, 1, false, new CancellationToken(true)))
            .Should().ThrowAsync<OperationCanceledException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbSearchCaches.Single().CachedAt.Should().Be(original.CachedAt);
    }

    [Fact]
    public async Task Season_BadIdentityOrDuplicateNumbersDoesNotReplaceGoodCache()
    {
        TmdbSeasonCatalogueResult original = await _sut.GetSeasonCatalogueAsync(42, 1);
        StubSeason(Season(2));
        (await _sut.GetSeasonCatalogueAsync(42, 1, true)).RefreshError.Should().NotBeNullOrEmpty();
        StubSeason(Season() with { Episodes = [Episode(1), Episode(1)] });
        TmdbSeasonCatalogueResult invalid = await _sut.GetSeasonCatalogueAsync(42, 1, true);
        invalid.CachedAt.Should().Be(original.CachedAt);
        invalid.Season!.Episodes.Should().ContainSingle();
        invalid.RefreshError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Season_EmptyResponseStaysEmptyAndMissingOverviewIsNotFabricated()
    {
        StubSeason(Season() with { Episodes = [] });
        (await _sut.GetSeasonCatalogueAsync(42, 1)).Season!.Episodes.Should().BeEmpty();
        StubSeason(Season() with { Episodes = [Episode(1) with { Overview = null }] });
        (await _sut.GetSeasonCatalogueAsync(42, 1, true)).Season!.Episodes.Single().Overview.Should().BeNull();
    }

    [Fact]
    public async Task RefreshErrorsNeverExposeRemoteBodyUrlOrSecrets()
    {
        StubSeasonFailure(new TmdbClientException("https://private.example/token?api_key=secret 机密路径", 503));
        (await _sut.GetSeasonCatalogueAsync(42, 1)).RefreshError.Should().Be("TMDB 服务返回错误（503），请稍后重试");
    }

    [Fact]
    public async Task CorruptCachedIdentityCannotBeReturnedAsFallback()
    {
        await _sut.GetDetailsFreshAsync(42, "tv");
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.TmdbMetadataCaches.Single().RawJson = "{\"id\":43}";
            db.SaveChanges();
        }
        StubDetailsFailure(new HttpRequestException("网络不可达"));
        TmdbDetailsResult result = await _sut.GetDetailsFreshAsync(42, "tv");
        result.CachedAt.Should().BeNull();
        result.Title.Should().BeNull();
        result.FromCache.Should().BeFalse();
        result.RefreshError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Details_ClientIgnoringCancellationCannotOverwriteGoodCache()
    {
        TmdbDetailsResult original = await _sut.GetDetailsFreshAsync(42, "tv");
        using CancellationTokenSource cts = new();
        _client.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => { cts.Cancel(); return Details("不应写入"); });
        await _sut.Invoking(s => s.GetDetailsFreshAsync(42, "tv", true, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Single().Title.Should().Be(original.Title);
        db.TmdbMetadataCaches.Single().CachedAt.Should().Be(original.CachedAt);
    }

    [Fact]
    public async Task Season_ClientIgnoringCancellationCannotOverwriteGoodCache()
    {
        TmdbSeasonCatalogueResult original = await _sut.GetSeasonCatalogueAsync(42, 1);
        using CancellationTokenSource cts = new();
        _client.GetSeasonAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => { cts.Cancel(); return Season(name: "不应写入"); });
        await _sut.Invoking(s => s.GetSeasonCatalogueAsync(42, 1, true, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbSearchCaches.Single().CachedAt.Should().Be(original.CachedAt);
        db.TmdbSearchCaches.Single().Results.Should().NotContain("不应写入");
    }

    [Fact]
    public async Task Details_ForceRefreshRecoversAfterFailure()
    {
        await _sut.GetDetailsFreshAsync(42, "tv");
        StubDetailsFailure(new TmdbClientException("失败", 503));
        (await _sut.GetDetailsFreshAsync(42, "tv", true)).RefreshError.Should().NotBeNullOrEmpty();
        StubDetails(Details("恢复标题"));
        TmdbDetailsResult recovered = await _sut.GetDetailsFreshAsync(42, "tv", true);
        recovered.RefreshError.Should().BeNull();
        recovered.FromCache.Should().BeFalse();
        (await _sut.GetDetailsFreshAsync(42, "tv")).Title.Should().Be("恢复标题");
    }

    [Fact]
    public async Task Season_ForceRefreshRecoversAfterFailure()
    {
        await _sut.GetSeasonCatalogueAsync(42, 1);
        StubSeasonFailure(new TmdbClientException("失败", 503));
        (await _sut.GetSeasonCatalogueAsync(42, 1, true)).RefreshError.Should().NotBeNullOrEmpty();
        StubSeason(Season(name: "恢复季名"));
        TmdbSeasonCatalogueResult recovered = await _sut.GetSeasonCatalogueAsync(42, 1, true);
        recovered.RefreshError.Should().BeNull();
        recovered.FromCache.Should().BeFalse();
        (await _sut.GetSeasonCatalogueAsync(42, 1)).Season!.Name.Should().Be("恢复季名");
    }

    [Fact]
    public async Task EmptySuccessfulDetailsCannotEraseGoodCache()
    {
        TmdbDetailsResult original = await _sut.GetDetailsFreshAsync(42, "tv");
        StubDetails(Details() with { Title = null, OriginalTitle = " ", RawJson = "{}", Seasons = null });
        TmdbDetailsResult result = await _sut.GetDetailsFreshAsync(42, "tv", true);
        result.RefreshError.Should().NotBeNullOrEmpty();
        result.Title.Should().Be(original.Title);
        result.CachedAt.Should().Be(original.CachedAt);
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Single().RawJson.Should().Be(original.RawJson);
    }

    [Theory]
    [InlineData("tv", "tv")]
    [InlineData(" TV\r\n", "tv")]
    [InlineData("Movie", "movie")]
    [InlineData("\tMOVIE\u2028", "movie")]
    public async Task Details_LogsOnlyCanonicalTypeAcrossRemoteCacheAndFailure(string input, string expected)
    {
        StubDetails(Details() with { MediaType = expected });
        (await _sut.GetDetailsFreshAsync(42, input)).MediaType.Should().Be(expected);
        (await _sut.GetDetailsFreshAsync(42, input)).FromCache.Should().BeTrue();
        StubDetailsFailure(new IOException("合成故障\r\n伪造日志：OK\t\u001b[31m"));
        (await _sut.GetDetailsFreshAsync(42, input, true)).FromCache.Should().BeTrue();
        _logger.Entries.Should().HaveCount(3);
        _logger.Entries.Should().OnlyContain(e => e.Message.Contains($"type={expected}") && e.Exception == null);
        _logger.AssertSingleLineWithout("伪造日志");
        _logger.Entries.Last().Message.Should().Contain("code=DetailsRefreshFailed");
    }

    [Theory]
    [InlineData("tv\r\n伪造日志")]
    [InlineData("tv\0")]
    [InlineData("mo\u001bvie")]
    [InlineData("tv\u2029movie")]
    public async Task Details_InvalidTypeIsRejectedWithoutRemoteCallOrOrdinaryLog(string input)
    {
        await _sut.Invoking(s => s.GetDetailsFreshAsync(42, input)).Should().ThrowAsync<BusinessException>();
        await _client.DidNotReceiveWithAnyArgs().GetDetailsAsync(default, default!, default!, default!, default);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Details_UntrustedErrorStaysInFullArtifactWithoutInjectingOrdinaryLogs()
    {
        const string raw = "合成故障\r\n伪造日志：OK\t\u001b[31m";
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-log-safety-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            string run = Guid.NewGuid().ToString("N");
            StubDetailsFailure(new TmdbClientException(raw, 503));
            using (ParseDiagnostics.Begin("metadata_log_test", run, sink: sink))
                (await _sut.GetDetailsFreshAsync(42, "TV", true)).RefreshError.Should().Contain("503");
            ParseReplayExport exported = sink.Export(run, null);
            exported.Artifacts.Should().ContainSingle(a => a.State == "recorded" && a.Text == raw);
            exported.Events.Single(e => e.Name == "tmdb.details_refresh_failed").Data.GetProperty("untrusted").GetBoolean().Should().BeTrue();
            _logger.AssertSingleLineWithout("伪造日志");
            _logger.Entries.Should().ContainSingle().Which.Exception.Should().BeNull();
        }
        finally { Directory.Delete(root, true); }
    }

    private void StubDetails(TmdbDetailsResult details) => _client.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(details);
    private void StubDetailsFailure(Exception ex) => _client.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<TmdbDetailsResult>(ex));
    private void StubSeason(TmdbSeasonDetail season) => _client.GetSeasonAsync(Arg.Any<int>(), Arg.Any<int>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(season);
    private void StubSeasonFailure(Exception ex) => _client.GetSeasonAsync(Arg.Any<int>(), Arg.Any<int>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<TmdbSeasonDetail>(ex));
    private void ExpireCaches()
    {
        using PmmDbContext db = _factory.CreateDbContext();
        foreach (TmdbSearchCache cache in db.TmdbSearchCaches) cache.CachedAt = DateTimeOffset.UtcNow.AddDays(-2);
        foreach (TmdbMetadataCache cache in db.TmdbMetadataCaches) cache.CachedAt = DateTimeOffset.UtcNow.AddDays(-2);
        db.SaveChanges();
    }
    private static TmdbDetailsResult Details(string title = "原标题") => new(42, "tv", title, null, 2024, 1,
        null, null, null, null, "简介", "{\"id\":42,\"seasons\":[{\"season_number\":1,\"episode_count\":1}]}", [new(1, 1)]);
    private static TmdbSeasonDetail Season(int number = 1, string name = "第一季") => new(number, name, null, null, null, [Episode(1)]);
    private static TmdbEpisodeRef Episode(int number) => new(number, $"第{number}集", "已知简介", null, null, null, null);
    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        public PmmDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection).Options);
    }
}
