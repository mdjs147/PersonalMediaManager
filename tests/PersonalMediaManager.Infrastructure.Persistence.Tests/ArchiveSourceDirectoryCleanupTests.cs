using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>只用合成临时目录验证源目录清理可选条件</summary>
public sealed class ArchiveSourceDirectoryCleanupTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Factory _factory;
    private readonly TestClock _clock = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pmm-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly long _categoryId;
    private readonly EmptyDirectoryCleaner _cleaner = new(NullLogger<EmptyDirectoryCleaner>.Instance);

    public ArchiveSourceDirectoryCleanupTests()
    {
        _connection.Open();
        _factory = new Factory(_connection);
        using PmmDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        PrivateFileSystem.EnsureDirectory(_root);
        _watch = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_watch);
        CategoryDefinition category = new() { Name = "测试", MediaType = MediaType.Both, TargetRoot = Path.Combine(_root, "library") };
        db.CategoryDefinitions.Add(category);
        db.WatchFolders.Add(new WatchFolder { Path = _watch });
        db.SaveChanges();
        _categoryId = category.Id;
    }

    [Theory]
    [InlineData(false, "true", "tv", "Returning Series", 0, true)]
    [InlineData(true, null, "tv", "Returning Series", 0, false)]
    [InlineData(true, "false", "tv", "Returning Series", 0, false)]
    [InlineData(true, "true", "tv", "Returning Series", 0, true)]
    [InlineData(true, "true", "tv", "Ended", 0, false)]
    [InlineData(true, "true", "tv", "Canceled", 0, false)]
    [InlineData(true, "true", "tv", "Unknown", 0, false)]
    [InlineData(true, "true", "tv", null, 0, false)]
    [InlineData(true, "true", "tv", "Returning Series", -25, false)]
    [InlineData(true, "true", "movie", "Returning Series", 0, false)]
    public async Task MasterOptionalFlagAndStatusPreserveCompatibility(
        bool master, string? option, string type, string? status, int cacheOffset, bool retained)
    {
        SetSetting("File.CleanEmptyDir", master ? "true" : "false");
        if (option is not null) SetSetting(OngoingSeriesDirectoryGuard.SettingKey, option);
        SeedMeta(1, type, status, cacheOffset);
        MediaItem item = SeedItem("show/episode.mkv", 1, type, realFile: true);
        string directory = Path.GetDirectoryName(item.SourcePath)!;
        File.WriteAllText(Path.Combine(directory, "download.torrent"), "合成残留");

        ArchiveResult result = await Sut().ArchiveAsync(item);

        result.Outcome.Should().Be(ArchiveOutcome.Completed);
        File.Exists(result.TargetPath).Should().BeTrue();
        File.Exists(item.SourcePath).Should().BeFalse();
        Directory.Exists(directory).Should().Be(retained);
        Directory.Exists(_watch).Should().BeTrue();
    }

    [Theory]
    [InlineData("Returning Series", true)]
    [InlineData("Unknown", false)]
    [InlineData("Ended", false)]
    public async Task LastMovieStillChecksHistoricalTvInMixedDirectory(string status, bool retained)
    {
        EnableGuard();
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", status);
        SeedItem("mixed/old-episode.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem("mixed/movie.mkv", 1, "movie", realFile: true);
        await Sut().ArchiveAsync(movie);
        Directory.Exists(Path.GetDirectoryName(movie.SourcePath)).Should().Be(retained);
    }

    [Fact]
    public async Task UpwardCleanupCannotDeleteProtectedSiblingThroughParent()
    {
        EnableGuard();
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Returning Series");
        MediaItem tv = SeedItem("mixed/series/old-episode.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem("mixed/movie/film.mkv", 1, "movie", realFile: true);
        await Sut().ArchiveAsync(movie);
        Directory.Exists(Path.GetDirectoryName(movie.SourcePath)).Should().BeFalse("独立电影子目录仍按旧规则清理");
        Directory.Exists(Path.GetDirectoryName(tv.SourcePath)).Should().BeTrue("祖先递归删除不能绕过剧集保护");
        Directory.Exists(Path.Combine(_watch, "mixed")).Should().BeTrue();
    }

    [Fact]
    public async Task SharedPathPrefixDoesNotProtectUnrelatedMovieDirectory()
    {
        EnableGuard();
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Returning Series");
        SeedItem("show-other/old.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem("show/movie.mkv", 1, "movie", realFile: true);
        await Sut().ArchiveAsync(movie);
        Directory.Exists(Path.GetDirectoryName(movie.SourcePath)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Ended", "Returning Series", true)]
    [InlineData("Returning Series", "Ended", false)]
    public async Task StatusIsRereadAtDeletionBoundary(string initial, string latest, bool retained)
    {
        EnableGuard();
        SeedMeta(1, "tv", initial);
        MediaItem item = SeedItem("show/episode.mkv", 1, "tv", realFile: true);
        var cleaner = new BeforeGuardCleaner(_cleaner, () =>
        {
            using PmmDbContext db = _factory.CreateDbContext();
            db.TmdbMetadataCaches.Single().RawJson = JsonSerializer.Serialize(new { id = 1, status = latest });
            db.SaveChanges();
        });
        await Sut(cleaner).ArchiveAsync(item);
        Directory.Exists(Path.GetDirectoryName(item.SourcePath)).Should().Be(retained);
    }

    [Fact]
    public async Task NonEmptySourceAndNestedWatchRootRemainProtected()
    {
        EnableGuard();
        SeedMeta(1, "tv", "Unknown");
        MediaItem item = SeedItem("nested/show/episode.mkv", 1, "tv", realFile: true);
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.WatchFolders.Add(new WatchFolder { Path = Path.Combine(_watch, "nested") });
            db.SaveChanges();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(item.SourcePath)!, "other.mkv"), "未处理视频");
        await Sut().ArchiveAsync(item);
        Directory.Exists(Path.GetDirectoryName(item.SourcePath)).Should().BeTrue();
        Directory.Exists(Path.Combine(_watch, "nested")).Should().BeTrue();
    }

    [Fact]
    public async Task MissingMetadataDoesNotTriggerOptionalProtection()
    {
        EnableGuard();
        MediaItem item = SeedItem("show/episode.mkv", 1, "tv", realFile: true);
        await Sut().ArchiveAsync(item);
        Directory.Exists(Path.GetDirectoryName(item.SourcePath)).Should().BeFalse();
    }

    [Fact]
    public async Task CopyOperationStillLeavesSourceDirectoryAndVideo()
    {
        EnableGuard();
        SeedMeta(1, "tv", "Ended");
        MediaItem item = SeedItem("show/episode.mkv", 1, "tv", realFile: true);
        await Sut().ArchiveAsync(item, ArchiveOperation.Copy);
        File.Exists(item.SourcePath).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(item.SourcePath)).Should().BeTrue();
    }

    [Fact]
    public async Task NestedWatchRootIsNeverRemovedWhenChildBecomesEmpty()
    {
        EnableGuard();
        SeedMeta(1, "tv", "Unknown");
        MediaItem item = SeedItem("nested/show/episode.mkv", 1, "tv", realFile: true);
        string nested = Path.Combine(_watch, "nested");
        using (PmmDbContext db = _factory.CreateDbContext())
        {
            db.WatchFolders.Add(new WatchFolder { Path = nested });
            db.SaveChanges();
        }
        await Sut().ArchiveAsync(item);
        Directory.Exists(Path.GetDirectoryName(item.SourcePath)).Should().BeFalse();
        Directory.Exists(nested).Should().BeTrue();
    }

    [Theory]
    [InlineData("File.CleanEmptyDir", true)]
    [InlineData("File.CleanEmptyDirKeepOngoingSeries", false)]
    public async Task SettingIsRereadImmediatelyBeforeCleanup(string key, bool retained)
    {
        EnableGuard();
        SeedMeta(1, "tv", "Returning Series");
        MediaItem item = SeedItem("show/episode.mkv", 1, "tv", realFile: true);
        var cleaner = new BeforeGuardCleaner(_cleaner, () => SetSetting(key, "false"));
        await Sut(cleaner).ArchiveAsync(item);
        Directory.Exists(Path.GetDirectoryName(item.SourcePath)).Should().Be(retained);
    }

    [WindowsFact]
    public async Task WindowsFilesystem_LastMoviePreservesUnicodeCaseEquivalentOngoingDirectory()
    {
        EnableGuard();
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Returning Series");
        MediaItem tv = SeedItem("Émission/old-episode.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem("émission/movie.mkv", 1, "movie", realFile: true);

        await Sut().ArchiveAsync(movie);

        File.Exists(movie.SourcePath).Should().BeFalse("电影应正常移入媒体库");
        Directory.Exists(Path.GetDirectoryName(movie.SourcePath)).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(tv.SourcePath)).Should().BeTrue();
    }

    [Fact]
    public async Task SqliteAsciiOnlyLikeReproduction_ExplicitWindowsComparisonFindsOngoingTv()
    {
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Returning Series");
        SeedItem("Émission/old-episode.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem("émission/movie.mkv", 1, "movie", realFile: true);
        string directory = Path.GetDirectoryName(movie.SourcePath)!;
        var scope = new SourceDirectoryCleanupScope(directory, StringComparison.OrdinalIgnoreCase);
        using PmmDbContext db = _factory.CreateDbContext();

        (await db.MediaItems.Where(m => m.TmdbMediaType == "tv" && m.SourcePath.StartsWith(scope.Prefix)).CountAsync())
            .Should().Be(0, "真实 SQLite LIKE 不支持 É/é 折叠，原粗筛会漏历史剧集");
        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);

        match.Should().NotBeNull();
        match!.TmdbId.Should().Be(2);
        File.Exists(movie.SourcePath).Should().BeTrue("本测试仅模拟 Windows 路径比较，不在 Linux 冒充 Windows 实机删除测试");
    }

    [Theory]
    [InlineData("mixed/series/old.mkv")]
    [InlineData("mixed\\series\\old.mkv")]
    [InlineData("mixed/series\\old.mkv")]
    [InlineData("mixed\\series/old.mkv")]
    public async Task HistoricalCandidateSeparatorsFollowNativePathSemantics(string historicalRelativePath)
    {
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Returning Series");
        MediaItem tv = SeedItem(historicalRelativePath, 2, "tv", realFile: false);
        MediaItem movie = SeedItem(Path.Combine("mixed", "series", "movie.mkv"), 1, "movie", realFile: true);
        var scope = SourceDirectoryCleanupScope.ForPlatform(Path.GetDirectoryName(movie.SourcePath)!);
        bool expected = OperatingSystem.IsWindows() || !historicalRelativePath.Contains('\\');
        scope.Contains(tv.SourcePath).Should().Be(expected, "Unix 的反斜杠是文件名字符，不能借 Windows 规范化扩大实际保护范围");
        using PmmDbContext db = _factory.CreateDbContext();

        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);

        (match is not null).Should().Be(expected, "SQL 宽筛不能遗漏平台支持的混合分隔符历史路径");
    }

    [Theory]
    [InlineData(@"C:\downloads\_mission\%", @"C:/downloads\Émission/old.mkv", true)]
    [InlineData(@"C:\downloads\_mission\%", @"C:\downloads/Émission\old.mkv", true)]
    [InlineData(@"C:\downloads\_mission\%", @"C:/downloads\Émission-other/old.mkv", false)]
    [InlineData(@"C:\downloads\the!_!%!!_mission\%", @"C:/downloads/the_%!Émission\old.mkv", true)]
    [InlineData(@"C:\downloads\the!_!%!!_mission\%", @"C:/downloads/the_anythingÉmission/old.mkv", false)]
    [InlineData(@"\\server\share\%", @"//server/share/Émission/old.mkv", true)]
    [InlineData(@"\\server\share\%", @"\\server/share\Émission/old.mkv", true)]
    [InlineData(@"\\server\share\%", @"//server/share-other/Émission/old.mkv", false)]
    public async Task SqliteWindowsPathPrefilterRequiresSeparatorNormalization(string pattern, string historicalPath, bool expected)
    {
        // 在任意系统复现 SQL 边界；只写字符串，不将此测试冒充 Windows 文件系统验证。
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaItems.Add(MediaItem.CreateFixture(historicalPath, "old.mkv", 10,
            status: MediaItemStatus.Completed, tmdbId: 2, tmdbMediaType: "tv"));
        db.SaveChanges();

        int originalCount = await db.MediaItems.CountAsync(m => EF.Functions.Like(m.SourcePath, pattern, "!"));
        int normalizedCount = await db.MediaItems.CountAsync(m => EF.Functions.Like(m.SourcePath.Replace("/", "\\"), pattern, "!"));

        originalCount.Should().Be(0, "SQLite 不会将正反斜杠视为相同分隔符");
        normalizedCount.Should().Be(expected ? 1 : 0, "规范化后仍须保留参数转义与目录边界");
    }

    [Fact]
    public async Task FilesystemRootScopeIncludesHistoricalSeriesWithoutDuplicateSeparator()
    {
        SeedMeta(2, "tv", "Returning Series");
        string root = Path.GetPathRoot(_watch)!;
        var scope = SourceDirectoryCleanupScope.ForPlatform(root);
        MediaItem tv = SeedItem("series/old.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem("movie.mkv", 1, "movie", realFile: true);
        scope.Prefix.Should().Be(root);
        scope.Contains(tv.SourcePath).Should().BeTrue();
        using PmmDbContext db = _factory.CreateDbContext();

        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);

        match.Should().NotBeNull();
        match!.TmdbId.Should().Be(2);
    }

    [WindowsTheory]
    [InlineData(@"C:\", @"C:/Émission/old.mkv", true)]
    [InlineData(@"C:\downloads\émission", @"C:/downloads\Émission/old.mkv", true)]
    [InlineData(@"C:\downloads\émission", @"C:\downloads/Émission\old.mkv", true)]
    [InlineData(@"C:\downloads\émission", @"C:/downloads\Émission-other/old.mkv", false)]
    [InlineData(@"C:\downloads\the_%!émission", @"C:/downloads/the_%!Émission\old.mkv", true)]
    [InlineData(@"C:\downloads\the_%!émission", @"C:/downloads/the_anythingÉmission/old.mkv", false)]
    [InlineData(@"\\server\share", @"//server/share/Émission/old.mkv", true)]
    [InlineData(@"\\server\share\", @"\\server/share\Émission/old.mkv", true)]
    [InlineData(@"\\server\share\", @"//server/share-other/Émission/old.mkv", false)]
    public async Task WindowsDriveAndUncCandidatesPreserveScopeBoundaries(string directory, string historicalPath, bool expected)
    {
        SeedMeta(2, "tv", "Returning Series");
        var scope = SourceDirectoryCleanupScope.ForPlatform(directory);
        scope.Contains(historicalPath).Should().Be(expected);
        // 只写合成历史行，不访问盘符或 UNC 共享，也不在这些路径执行清理。
        MediaItem tv = MediaItem.CreateFixture(historicalPath, "old.mkv", 10,
            status: MediaItemStatus.Completed, tmdbId: 2, tmdbMediaType: "tv");
        MediaItem movie = MediaItem.CreateFixture(Path.Combine(directory, "movie.mkv"), "movie.mkv", 10,
            status: MediaItemStatus.Archiving, tmdbId: 1, tmdbMediaType: "movie");
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaItems.Add(tv);
        db.SaveChanges();

        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);

        (match is not null).Should().Be(expected);
    }

    [Theory]
    [InlineData("Émission", "émission", StringComparison.OrdinalIgnoreCase, "Returning Series", true)]
    [InlineData("Émission", "émission", StringComparison.Ordinal, "Returning Series", false)]
    [InlineData("Show", "show", StringComparison.OrdinalIgnoreCase, "Returning Series", true)]
    [InlineData("Show", "show", StringComparison.Ordinal, "Returning Series", false)]
    [InlineData("Ämission", "émission", StringComparison.OrdinalIgnoreCase, "Returning Series", false)]
    [InlineData("émission-other", "émission", StringComparison.OrdinalIgnoreCase, "Returning Series", false)]
    [InlineData("the_%!Émission", "the_%!émission", StringComparison.OrdinalIgnoreCase, "Returning Series", true)]
    [InlineData("𐐀mission", "𐐨mission", StringComparison.OrdinalIgnoreCase, "Returning Series", true)]
    [InlineData("Émission", "émission", StringComparison.OrdinalIgnoreCase, "Unknown", false)]
    [InlineData("Émission", "émission", StringComparison.OrdinalIgnoreCase, "Ended", false)]
    public async Task MixedMovieTvCleanupScopeUsesExplicitPlatformComparison(
        string tvDirectory, string movieDirectory, StringComparison comparison, string status, bool expected)
    {
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", status);
        SeedItem(tvDirectory + "/old-episode.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem(movieDirectory + "/movie.mkv", 1, "movie", realFile: true);
        var scope = new SourceDirectoryCleanupScope(Path.GetDirectoryName(movie.SourcePath)!, comparison);
        using PmmDbContext db = _factory.CreateDbContext();
        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);
        (match is not null).Should().Be(expected);
    }

    [Theory]
    [InlineData("ſeries", "series")]
    [InlineData("Katalog", "katalog")]
    [InlineData("ıtem", "item")]
    public async Task NonAsciiToAsciiPairsFollowRuntimeOrdinalSemantics(string tvDirectory, string movieDirectory)
    {
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Returning Series");
        MediaItem tv = SeedItem(tvDirectory + "/old.mkv", 2, "tv", realFile: false);
        MediaItem movie = SeedItem(movieDirectory + "/movie.mkv", 1, "movie", realFile: true);
        var scope = new SourceDirectoryCleanupScope(Path.GetDirectoryName(movie.SourcePath)!, StringComparison.OrdinalIgnoreCase);
        using PmmDbContext db = _factory.CreateDbContext();
        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);
        (match is not null).Should().Be(scope.Contains(tv.SourcePath), "SQL 粗筛不能排除运行时序数比较真正相等的路径");
    }

    [Fact]
    public async Task CandidatePagesRemainBoundedAndDoNotMissLaterOngoingSeries()
    {
        SeedMeta(1, "movie", null);
        SeedMeta(2, "tv", "Unknown");
        SeedMeta(3, "tv", "Returning Series");
        for (int i = 0; i < OngoingSeriesDirectoryGuard.CandidatePageSize; i++)
            SeedItem($"Émission/old-{i}.mkv", 2, "tv", realFile: false);
        SeedItem("Émission/last-ongoing.mkv", 3, "tv", realFile: false);
        MediaItem movie = SeedItem("émission/movie.mkv", 1, "movie", realFile: true);
        var scope = new SourceDirectoryCleanupScope(Path.GetDirectoryName(movie.SourcePath)!, StringComparison.OrdinalIgnoreCase);
        var capture = new CandidateQueryCapture();
        _factory.Capture = capture;
        using PmmDbContext db = _factory.CreateDbContext();

        TmdbMetadataCache? match = await OngoingSeriesDirectoryGuard.FindForDirectoryAsync(db, scope, movie, _clock.UtcNow, default);

        match!.TmdbId.Should().Be(3, "不能以固定总条数截断，把后面的明确未完结剧集变成未知");
        capture.CandidateCommands.Should().HaveCount(2);
        foreach ((string sql, int limit) in capture.CandidateCommands)
        {
            sql.Should().Contain("LIKE").And.Contain("ORDER BY").And.Contain("LIMIT").And.Contain(" > ");
            limit.Should().Be(OngoingSeriesDirectoryGuard.CandidatePageSize);
        }
    }

    private void EnableGuard()
    {
        SetSetting("File.CleanEmptyDir", "true");
        SetSetting(OngoingSeriesDirectoryGuard.SettingKey, "true");
    }

    private void SetSetting(string key, string value)
    {
        using PmmDbContext db = _factory.CreateDbContext();
        SystemSetting? row = db.SystemSettings.Find(key);
        if (row is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "General" });
        else row.Value = value;
        db.SaveChanges();
    }

    private void SeedMeta(int id, string type, string? status, int offset = 0)
    {
        using PmmDbContext db = _factory.CreateDbContext();
        db.TmdbMetadataCaches.Add(new TmdbMetadataCache
        {
            TmdbId = id, MediaType = type, Title = "合成作品" + id, Year = 2026,
            RawJson = JsonSerializer.Serialize(new { id, status }), CachedAt = _clock.UtcNow.AddHours(offset),
        });
        db.SaveChanges();
    }

    private MediaItem SeedItem(string relative, int id, string type, bool realFile)
    {
        string path = Path.Combine(_watch, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (realFile) File.WriteAllText(path, "合成视频");
        MediaItem item = MediaItem.CreateFixture(path, Path.GetFileName(path), 10,
            status: realFile ? MediaItemStatus.Archiving : MediaItemStatus.Completed,
            tmdbId: id, tmdbMediaType: type, categoryId: _categoryId,
            parsedInfo: JsonSerializer.Serialize(new { title = "合成作品" + id, year = 2026, type, season = 1, episode = 1 }));
        using PmmDbContext db = _factory.CreateDbContext();
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item;
    }

    private ArchiveService Sut(IEmptyDirectoryCleaner? cleaner = null) => new(
        _factory, new FileMover(NullLogger<FileMover>.Instance), Substitute.For<IAudioRemuxer>(), cleaner ?? _cleaner,
        Substitute.For<IWebhookOutboxQueue>(), _clock, Substitute.For<IPosterDownloader>(), AppPaths.ForRoot(_root),
        NullLogger<ArchiveService>.Instance);

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "需要 Windows 实际文件系统；Linux 仅验证独立路径比较与 SQLite 查询语义";
        }
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "需要 Windows 路径规范化语义；不访问合成的盘符或 UNC 共享";
        }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        internal CandidateQueryCapture? Capture { get; set; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> options = new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection);
            if (Capture is not null) options.AddInterceptors(Capture);
            return new PmmDbContext(options.Options);
        }
    }

    private sealed class CandidateQueryCapture : DbCommandInterceptor
    {
        internal List<(string Sql, int Limit)> CandidateCommands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Media_Item\"", StringComparison.Ordinal))
            {
                string parameterName = command.CommandText.Split("LIMIT ")[^1].Trim();
                int limit = Convert.ToInt32(command.Parameters[parameterName].Value);
                CandidateCommands.Add((command.CommandText, limit));
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class BeforeGuardCleaner(IEmptyDirectoryCleaner inner, Action before) : IEmptyDirectoryCleaner
    {
        public IReadOnlyList<string> CleanUpward(string start, string boundary, IReadOnlySet<string> ignored, CancellationToken ct = default)
            => inner.CleanUpward(start, boundary, ignored, ct);
        public Task<IReadOnlyList<string>> CleanUpwardAsync(string start, string boundary, IReadOnlySet<string> ignored,
            Func<string, CancellationToken, Task<bool>> guard, CancellationToken ct = default)
            => inner.CleanUpwardAsync(start, boundary, ignored, (path, token) => { before(); return guard(path, token); }, ct);
    }
}
