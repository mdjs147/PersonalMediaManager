using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Library;
using PersonalMediaManager.Application.Services.Library;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Library;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>媒体库 LibraryService — 作品聚合 / 过滤 / 分页 / 海报路径</summary>
public sealed class LibraryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _scratch;
    private readonly AppPaths _paths;
    private readonly IMediaAudioProbe _audioProbe;
    private readonly IMediaExtensionProvider _extensions;
    private readonly IFileIntakeService _intake;
    private readonly LibraryService _sut;

    public LibraryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _scratch = Path.Combine(Path.GetTempPath(), $"pmm-lib-{Guid.NewGuid():N}");
        _paths = AppPaths.ForRoot(_scratch);
        // 富化服务在列表/详情路径仅做惰性触发，单测用 NSubstitute 空实现（不发网络、不建 Media_Work）
        IWorkEnrichmentService enrichment = Substitute.For<IWorkEnrichmentService>();
        _audioProbe = Substitute.For<IMediaAudioProbe>();
        _extensions = Substitute.For<IMediaExtensionProvider>();
        _extensions.GetEnabledExtensions().Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi" });
        _intake = Substitute.For<IFileIntakeService>();
        _intake.AdmitAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<PendingFileSource>(), Arg.Any<CancellationToken>()).Returns(true);
        _sut = new LibraryService(_dbFactory, enrichment, _audioProbe, _extensions, _intake, _paths, NullLogger<LibraryService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    [Fact(DisplayName = "媒体库：剧集多集聚合成一张卡片，FileCount = 集数")]
    public async Task Aggregates_Tv_Episodes_Into_Single_Card()
    {
        Seed(1396, "tv", "绝命毒师", 2008, MediaItemStatus.Completed, season: 1, episode: 1);
        Seed(1396, "tv", "绝命毒师", 2008, MediaItemStatus.Completed, season: 1, episode: 2);
        Seed(1396, "tv", "绝命毒师", 2008, MediaItemStatus.Completed, season: 1, episode: 3);

        LibraryListPage page = await _sut.ListAsync(new LibraryListQuery());

        page.Total.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items[0].TmdbId.Should().Be(1396);
        page.Items[0].MediaType.Should().Be("tv");
        page.Items[0].Title.Should().Be("绝命毒师");
        page.Items[0].FileCount.Should().Be(3);
    }

    [Fact(DisplayName = "媒体库：电影一对一一张卡片")]
    public async Task Movie_Single_Card()
    {
        Seed(27205, "movie", "盗梦空间", 2010, MediaItemStatus.Completed);

        LibraryListPage page = await _sut.ListAsync(new LibraryListQuery());

        page.Total.Should().Be(1);
        page.Items[0].FileCount.Should().Be(1);
        page.Items[0].Year.Should().Be(2010);
    }

    [Fact(DisplayName = "媒体库：仅纳入 Completed，排除在途/待审/失败")]
    public async Task Excludes_NonCompleted()
    {
        Seed(1, "movie", "已完成", 2020, MediaItemStatus.Completed);
        Seed(2, "movie", "待审", 2020, MediaItemStatus.AwaitingReview);
        Seed(3, "movie", "失败", 2020, MediaItemStatus.Failed);

        LibraryListPage page = await _sut.ListAsync(new LibraryListQuery());

        page.Total.Should().Be(1);
        page.Items[0].TmdbId.Should().Be(1);
    }

    [Fact(DisplayName = "媒体库：排除无 TmdbId 的记录")]
    public async Task Excludes_Null_TmdbId()
    {
        Seed(null, "movie", "未匹配", 2020, MediaItemStatus.Completed);
        Seed(10, "movie", "已匹配", 2020, MediaItemStatus.Completed);

        LibraryListPage page = await _sut.ListAsync(new LibraryListQuery());

        page.Total.Should().Be(1);
        page.Items[0].TmdbId.Should().Be(10);
    }

    [Fact(DisplayName = "媒体库：按类型过滤")]
    public async Task Filter_By_Type()
    {
        Seed(1, "movie", "电影一", 2020, MediaItemStatus.Completed);
        Seed(2, "tv", "剧集一", 2019, MediaItemStatus.Completed, season: 1, episode: 1);

        LibraryListPage movies = await _sut.ListAsync(new LibraryListQuery(Type: "movie"));
        LibraryListPage tvs = await _sut.ListAsync(new LibraryListQuery(Type: "tv"));

        movies.Items.Should().ContainSingle().Which.MediaType.Should().Be("movie");
        tvs.Items.Should().ContainSingle().Which.MediaType.Should().Be("tv");
    }

    [Fact(DisplayName = "媒体库：按关键词过滤标题")]
    public async Task Filter_By_Keyword()
    {
        Seed(1, "movie", "盗梦空间", 2010, MediaItemStatus.Completed);
        Seed(2, "movie", "星际穿越", 2014, MediaItemStatus.Completed);

        LibraryListPage page = await _sut.ListAsync(new LibraryListQuery(Keyword: "星际"));

        page.Items.Should().ContainSingle().Which.Title.Should().Be("星际穿越");
    }

    [Fact(DisplayName = "媒体库：分页限制每页数量")]
    public async Task Pagination_Limits_Items()
    {
        for (int i = 1; i <= 5; i++)
            Seed(i, "movie", $"片{i}", 2000 + i, MediaItemStatus.Completed);

        LibraryListPage page1 = await _sut.ListAsync(new LibraryListQuery(Page: 1, PageSize: 2));

        page1.Total.Should().Be(5);
        page1.Items.Should().HaveCount(2);
        page1.PageSize.Should().Be(2);
    }

    [Fact(DisplayName = "海报路径：无缓存文件返回 null")]
    public void GetPosterPath_Missing_Returns_Null()
    {
        _sut.GetPosterPath(99999).Should().BeNull();
    }

    [Fact(DisplayName = "海报路径：有缓存文件返回该路径")]
    public void GetPosterPath_Existing_Returns_Path()
    {
        string poster = Path.Combine(_paths.PostersDir, "27205.jpg");
        File.WriteAllText(poster, "fake-jpeg");

        _sut.GetPosterPath(27205).Should().Be(poster);
    }

    // ---------- 作品详情 GetWorkDetailAsync ----------

    [Fact(DisplayName = "作品详情：聚合同剧全部记录（含失败兄弟集），按季/集排序 + 计数 + 总大小")]
    public async Task WorkDetail_Aggregates_Files_With_Counts_And_Sorting()
    {
        // 乱序入库：E2 / E1 / E3（Completed）+ E4（Failed）；各 10 字节
        Seed(100, "tv", "繁花", 2023, MediaItemStatus.Completed, season: 1, episode: 2, fileSize: 10);
        Seed(100, "tv", "繁花", 2023, MediaItemStatus.Completed, season: 1, episode: 1, fileSize: 10);
        Seed(100, "tv", "繁花", 2023, MediaItemStatus.Completed, season: 1, episode: 3, fileSize: 10);
        Seed(100, "tv", "繁花", 2023, MediaItemStatus.Failed, season: 1, episode: 4, fileSize: 10);
        // 不同作品 + 不同类型，必须被排除
        Seed(200, "tv", "别的剧", 2020, MediaItemStatus.Completed, season: 1, episode: 1);

        LibraryWorkDetailResponse d = await _sut.GetWorkDetailAsync(100, "tv");

        d.TmdbId.Should().Be(100);
        d.MediaType.Should().Be("tv");
        d.FileCount.Should().Be(4);
        d.CompletedCount.Should().Be(3);
        d.FailedCount.Should().Be(1);
        d.AwaitingReviewCount.Should().Be(0);
        d.TotalFileSize.Should().Be(40);
        d.Files.Select(f => f.Episode).Should().ContainInOrder(1, 2, 3, 4);
        d.Files[3].Status.Should().Be(MediaItemStatus.Failed);
    }

    [Fact(DisplayName = "作品详情：命中 TMDB 缓存时用缓存元数据（标题/简介/原产国/季数）")]
    public async Task WorkDetail_Uses_Tmdb_Cache_Metadata_When_Present()
    {
        Seed(100, "tv", "解析标题", 2008, MediaItemStatus.Completed, season: 1, episode: 1);
        SeedTmdbCache(100, "tv", "缓存标题", overview: "一段简介", originCountry: new List<string> { "CN", "HK" }, totalSeasons: 5);

        LibraryWorkDetailResponse d = await _sut.GetWorkDetailAsync(100, "tv");

        d.Title.Should().Be("缓存标题");
        d.Overview.Should().Be("一段简介");
        d.OriginCountry.Should().ContainInOrder("CN", "HK");
        d.TotalSeasons.Should().Be(5);
    }

    [Fact(DisplayName = "作品详情：无 TMDB 缓存时回退解析标题/年份")]
    public async Task WorkDetail_Falls_Back_To_ParsedInfo_When_No_Cache()
    {
        Seed(100, "movie", "盗梦空间", 2010, MediaItemStatus.Completed);

        LibraryWorkDetailResponse d = await _sut.GetWorkDetailAsync(100, "movie");

        d.Title.Should().Be("盗梦空间");
        d.Year.Should().Be(2010);
    }

    [Fact(DisplayName = "作品详情：无任何记录抛 BusinessException")]
    public async Task WorkDetail_No_Records_Throws()
    {
        await Assert.ThrowsAsync<BusinessException>(() => _sut.GetWorkDetailAsync(99999, "tv"));
    }

    // ---------- 维度过滤 / 存在性扫描 ----------

    [Fact(DisplayName = "媒体库：按类型(genre)过滤命中连接表，仅返回该类型作品")]
    public async Task Filter_By_GenreId_Hits_Join()
    {
        Seed(100, "tv", "剧A", 2020, MediaItemStatus.Completed, season: 1, episode: 1);
        Seed(200, "tv", "剧B", 2020, MediaItemStatus.Completed, season: 1, episode: 1);
        SeedWorkWithGenre(100, "tv", 18, "剧情");
        SeedWorkWithGenre(200, "tv", 80, "犯罪");

        LibraryListPage page = await _sut.ListAsync(new LibraryListQuery(GenreId: 18));

        page.Items.Should().ContainSingle().Which.TmdbId.Should().Be(100);
    }

    [Fact(DisplayName = "存在性扫描：缺失文件标记 FileMissing，存在文件不标记")]
    public async Task ScanExistence_Marks_Missing()
    {
        Directory.CreateDirectory(_scratch);
        string present = Path.Combine(_scratch, "present.mkv");
        File.WriteAllText(present, "x");
        Seed(1, "movie", "存在", 2020, MediaItemStatus.Completed, targetPath: present);
        Seed(2, "movie", "缺失", 2020, MediaItemStatus.Completed, targetPath: Path.Combine(_scratch, "gone.mkv"));

        ScanExistenceResult r = await _sut.ScanExistenceAsync();

        r.Checked.Should().Be(2);
        r.Missing.Should().Be(1);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        (await db.MediaItems.CountAsync(m => m.FileMissing)).Should().Be(1);
    }

    // ---------- helpers ----------

    // ---------- 存量音频重扫 ----------

    [Fact(DisplayName = "存量音频重扫：探测含 av3a 的已归档文件并打标")]
    public async Task RescanAudio_Probes_And_Flags_Incompatible()
    {
        string ffmpegDir = SeedFakeFfprobe();
        string mediaFile = Path.Combine(_scratch, "movie.mkv");
        await File.WriteAllTextAsync(mediaFile, "x");
        SeedAudioSettings(ffmpegDir);
        Seed(100, "movie", "片子", 2020, MediaItemStatus.Completed, targetPath: mediaFile);

        _audioProbe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AudioProbeResult.Ok(new[]
            {
                new AudioStreamInfo(1, "av3a", "chi", 6, null, true),
                new AudioStreamInfo(2, "aac", "eng", 2, null, false),
            }));

        AudioRescanResult r = await _sut.RescanAudioAsync();

        r.Checked.Should().Be(1);
        r.Probed.Should().Be(1);
        r.Incompatible.Should().Be(1);
        r.Skipped.Should().Be(0);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = db.MediaItems.AsNoTracking().Single(x => x.TmdbId == 100);
        m.AudioCodecs.Should().Be("av3a,aac");
        m.HasIncompatibleAudio.Should().BeTrue();
    }

    [Fact(DisplayName = "存量音频重扫：探测不可用(缺文件)跳过不打标")]
    public async Task RescanAudio_Skips_Unavailable()
    {
        string ffmpegDir = SeedFakeFfprobe();
        SeedAudioSettings(ffmpegDir);
        Seed(101, "movie", "缺文件", 2020, MediaItemStatus.Completed, targetPath: "/nonexistent/gone.mkv");

        _audioProbe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AudioProbeResult.Unavailable("文件不存在"));

        AudioRescanResult r = await _sut.RescanAudioAsync();

        r.Checked.Should().Be(1);
        r.Probed.Should().Be(0);
        r.Skipped.Should().Be(1);
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.AsNoTracking().Single(x => x.TmdbId == 101).AudioCodecs.Should().BeNull();
    }

    [Fact(DisplayName = "存量音频重扫：只扫未探测过(AudioCodecs==null)且 Completed 的记录")]
    public async Task RescanAudio_Only_Untested_Completed()
    {
        string ffmpegDir = SeedFakeFfprobe();
        string mediaFile = Path.Combine(_scratch, "m.mkv");
        await File.WriteAllTextAsync(mediaFile, "x");
        SeedAudioSettings(ffmpegDir);
        Seed(200, "movie", "待审(排除)", 2020, MediaItemStatus.AwaitingReview, targetPath: mediaFile);
        Seed(201, "movie", "已完成待探", 2020, MediaItemStatus.Completed, targetPath: mediaFile);

        _audioProbe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AudioProbeResult.Ok(new[] { new AudioStreamInfo(1, "aac", null, 2, null, false) }));

        AudioRescanResult r = await _sut.RescanAudioAsync();

        r.Checked.Should().Be(1, "仅 Completed 且 AudioCodecs==null 纳入，AwaitingReview 排除");
    }

    [Fact(DisplayName = "存量音频重扫：未启用开关抛 BusinessException")]
    public async Task RescanAudio_Throws_When_Disabled()
    {
        // EnsureCreated 已种入 Audio_IncompatibleCheckEnabled 默认 false → 视为关闭
        Func<Task> act = () => _sut.RescanAudioAsync();
        await act.Should().ThrowAsync<BusinessException>();
    }

    // ---------- 孤儿文件找回 ----------

    [Fact(DisplayName = "孤儿扫描：分类目录有文件但无记录 → 列为孤儿；有记录与非媒体文件排除")]
    public async Task ScanOrphans_Lists_Files_Without_Record()
    {
        string root = Path.Combine(_scratch, "movies");
        Directory.CreateDirectory(root);
        SeedCategory(root, "电影");
        // A：磁盘有 + 有 Completed 记录（TargetPath 指向它）→ 非孤儿
        string archived = Path.Combine(root, "A (2020) {tmdb-1}", "A (2020) {tmdb-1}.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        await File.WriteAllBytesAsync(archived, new byte[10]);
        Seed(1, "movie", "A", 2020, MediaItemStatus.Completed, targetPath: archived);
        // B：磁盘有 + 无记录 → 孤儿
        string orphan = Path.Combine(root, "B (2021) {tmdb-2}", "B (2021) {tmdb-2}.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, new byte[20]);
        // C：非媒体文件 → 忽略
        await File.WriteAllBytesAsync(Path.Combine(root, "note.txt"), new byte[5]);

        OrphanScanResult r = await _sut.ScanOrphansAsync();

        r.Items.Should().ContainSingle();
        r.Items[0].FileName.Should().Be("B (2021) {tmdb-2}.mkv");
        r.Items[0].TmdbId.Should().Be(2, "从 {tmdb-2} 标记解析");
        r.Items[0].CategoryName.Should().Be("电影");
    }

    [Fact(DisplayName = "孤儿认领：存在的文件入队、不存在的跳过；空请求抛错")]
    public async Task ClaimOrphans_Admits_Existing_Skips_Missing()
    {
        string root = Path.Combine(_scratch, "movies2");
        Directory.CreateDirectory(root);
        string exist = Path.Combine(root, "X.mkv");
        await File.WriteAllBytesAsync(exist, new byte[10]);

        ClaimOrphansResult r = await _sut.ClaimOrphansAsync(
            new ClaimOrphansRequest(new[] { exist, Path.Combine(root, "gone.mkv") }));

        r.Total.Should().Be(2);
        r.Admitted.Should().Be(1);
        r.Skipped.Should().Be(1);
        await _intake.Received(1).AdmitAsync(exist, 0, PendingFileSource.Manual, Arg.Any<CancellationToken>());

        Func<Task> empty = () => _sut.ClaimOrphansAsync(new ClaimOrphansRequest());
        await empty.Should().ThrowAsync<BusinessException>();
    }

    /// <summary>造一个分类（孤儿扫描读其 TargetRoot）</summary>
    private void SeedCategory(string targetRoot, string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.CategoryDefinitions.Add(new CategoryDefinition
        {
            Name = name,
            MediaType = MediaType.Both,
            TargetRoot = targetRoot,
            Priority = 100,
        });
        db.SaveChanges();
    }

    /// <summary>临时目录建假 ffprobe（让 ResolveFfmpegTools 的 File.Exists 通过；探测由 mock 接管）</summary>
    private string SeedFakeFfprobe()
    {
        string dir = Path.Combine(_scratch, "ffmpeg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"), "");
        return dir;
    }

    /// <summary>把 Audio 设置更新为「启用 + 配置 ffmpeg 路径 + av3a 清单」</summary>
    private void SeedAudioSettings(string ffmpegDir)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        UpsertSetting(db, "Audio_IncompatibleCheckEnabled", "true");
        UpsertSetting(db, "Audio_FfmpegPath", ffmpegDir);
        UpsertSetting(db, "Audio_IncompatibleCodecs", "av3a");
        db.SaveChanges();

        static void UpsertSetting(PmmDbContext db, string key, string val)
        {
            SystemSetting? s = db.SystemSettings.FirstOrDefault(x => x.Key == key);
            if (s is not null) s.Value = val;
            else db.SystemSettings.Add(new SystemSetting { Key = key, Value = val, Category = "Audio" });
        }
    }

    private void Seed(int? tmdbId, string type, string title, int? year, MediaItemStatus status,
        int? season = null, int? episode = null, long? categoryId = null, long fileSize = 1, string? targetPath = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem m = MediaItem.CreateFixture(
            sourcePath: $"/m/{Guid.NewGuid():N}.mkv",
            fileName: $"{title}.mkv",
            fileSize: fileSize,
            status: status,
            tmdbId: tmdbId,
            tmdbMediaType: tmdbId is null ? null : type,
            parseSource: ParseSource.Rule,
            parsedInfo: new ParsedInfo(title, year, type, season, episode, null, null).ToJson(),
            categoryId: categoryId,
            targetPath: targetPath);
        db.MediaItems.Add(m);
        db.SaveChanges();
    }

    /// <summary>种一部富化作品（Media_Work + 一个类型连接），用于维度过滤/筛选项测试</summary>
    private void SeedWorkWithGenre(int tmdbId, string type, int genreId, string genreName)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        if (!db.MediaGenres.Any(g => g.Id == genreId))
            db.MediaGenres.Add(new MediaGenre { Id = genreId, Name = genreName });
        MediaWork w = MediaWork.CreateMinimal(tmdbId, type, $"作品{tmdbId}", 2020);
        db.MediaWorks.Add(w);
        db.SaveChanges();
        w.ReplaceGenres(new[] { genreId });
        db.SaveChanges();
    }

    private void SeedTmdbCache(int tmdbId, string type, string title, string? overview = null,
        List<string>? originCountry = null, int? totalSeasons = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.TmdbMetadataCaches.Add(new TmdbMetadataCache
        {
            TmdbId = tmdbId,
            MediaType = type,
            Title = title,
            OriginalTitle = $"{title} (orig)",
            Year = 2008,
            TotalSeasons = totalSeasons,
            Overview = overview,
            OriginCountry = originCountry,
            OriginalLanguage = "en",
            CachedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }
}
