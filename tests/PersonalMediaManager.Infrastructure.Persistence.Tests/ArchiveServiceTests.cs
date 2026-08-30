using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D7.4 ArchiveService — 电影 / 剧集 / 冲突 / 字幕 / nfo / Webhook 出 Outbox</summary>
/// <remarks>
/// 用真实 FileMover + 真实 PlexNamingConventions + 临时文件系统跑端到端，验证：
///   - 落到正确 Plex 命名路径
///   - nfo 已写 + XML 合法
///   - 同 stem 字幕同步重命名
///   - 同名冲突 → ConflictSkipped 早返不动文件
///   - Webhook subscriptions 订阅 media.archived 时入 outbox；未订阅跳过
/// </remarks>
public sealed class ArchiveServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IFileMover _fileMover = new FileMover(NullLogger<FileMover>.Instance);
    private readonly IAudioRemuxer _audioRemuxer = Substitute.For<IAudioRemuxer>();
    private readonly IEmptyDirectoryCleaner _emptyDirCleaner = new EmptyDirectoryCleaner(NullLogger<EmptyDirectoryCleaner>.Instance);
    private readonly FakeWebhookOutboxQueue _outboxQueue = new();
    private readonly FakeClock _clock = new();
    private readonly IPosterDownloader _poster = Substitute.For<IPosterDownloader>();
    private readonly string _scratch;
    private readonly string _sourceDir;
    private readonly string _targetRoot;
    private readonly ArchiveService _sut;

    public ArchiveServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _scratch = Path.Combine(Path.GetTempPath(), $"pmm-arch-{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_scratch, "src");
        _targetRoot = Path.Combine(_scratch, "library");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetRoot);

        _sut = new ArchiveService(_dbFactory, _fileMover, _audioRemuxer, _emptyDirCleaner, _outboxQueue, _clock,
            _poster, AppPaths.ForRoot(_scratch), NullLogger<ArchiveService>.Instance);
    }

    /// <summary>用自定义 IFileMover 构造 ArchiveService（注入字幕移动失败 / 真取消等场景）</summary>
    private ArchiveService CreateSutWith(IFileMover mover)
        => new(_dbFactory, mover, _audioRemuxer, _emptyDirCleaner, _outboxQueue, _clock, _poster, AppPaths.ForRoot(_scratch), NullLogger<ArchiveService>.Instance);

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ---------- 电影 ----------

    [Fact]
    public async Task Movie_HappyPath_Moves_To_Plex_Path_And_Writes_Nfo()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en",
                 genres: """[{"id":28,"name":"Action"},{"id":878,"name":"Science Fiction"}]""",
                 plot: "A thief who steals corporate secrets...");
        string videoPath = WriteFile("Inception.2010.1080p.BluRay.x264.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().EndWith(Path.Combine("盗梦空间 (2010) {tmdb-27205}", "盗梦空间 (2010) {tmdb-27205}.mkv"));
        File.Exists(r.TargetPath).Should().BeTrue();
        File.Exists(videoPath).Should().BeFalse("Move 已搬走源文件");

        string nfoPath = Path.ChangeExtension(r.TargetPath, ".nfo");
        File.Exists(nfoPath).Should().BeTrue();
        string nfo = await File.ReadAllTextAsync(nfoPath);
        nfo.Should().Contain("<movie>").And.Contain("盗梦空间").And.Contain("Inception").And.Contain("27205");
    }

    // ---------- 落地后元数据失败容错（步骤8/9 降级，不整条判 Failed） ----------

    [Fact(DisplayName = "移视频成功后写 nfo 失败 → 视频已落地、源已删、Outcome=Completed + Warnings 标记待补元数据（不判 Failed）")]
    public async Task Nfo_Write_Fails_After_Video_Moved_DegradesTo_Warning_Not_Failed()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en", genres: "[]");

        // 注入 nfo 写入失败：在目标 nfo 路径处预置一个「同名目录」。视频 Move 落地后，nfo 写文件到该路径
        // （File.WriteAllBytes 写入一个目录）必抛 UnauthorizedAccessException —— 模拟「视频已落地，但目标盘
        // 瞬时只读 / 网络盘断开导致 nfo 写不出」。Directory.CreateDirectory 连带建出归档文件夹。
        string showFolder = Path.Combine(_targetRoot, "盗梦空间 (2010) {tmdb-27205}");
        string nfoAsDir = Path.Combine(showFolder, "盗梦空间 (2010) {tmdb-27205}.nfo");
        Directory.CreateDirectory(nfoAsDir);

        string videoPath = WriteFile("Inception.2010.1080p.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        // 视频落地 = 归档主体成功；nfo 失败绝不整条判 Failed（否则源已删、Rescan 不可恢复）
        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetVideo = Path.Combine(showFolder, "盗梦空间 (2010) {tmdb-27205}.mkv");
        File.Exists(targetVideo).Should().BeTrue("视频已落地到目标位");
        File.Exists(videoPath).Should().BeFalse("Move 已搬走源文件——源已不在，故必须不判 Failed");

        // nfo 失败降级为 Warning 收集进 Warnings，供编排层标记「待补元数据」
        r.Warnings.Should().NotBeNullOrEmpty();
        r.Warnings!.Should().ContainSingle().Which.Should().Contain("nfo");
        // nfo 路径被目录占用 → 未写出真正的 .nfo 文件
        File.Exists(Path.ChangeExtension(targetVideo, ".nfo")).Should().BeFalse("nfo 写入失败，未生成文件");
    }

    [Fact(DisplayName = "归档全部成功 → Warnings 为空（与降级路径对照）")]
    public async Task Successful_Archive_Has_No_Warnings()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Inception.2010.1080p.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.Warnings.Should().BeNullOrEmpty("nfo / Webhook 均成功时无待补元数据");
    }

    // ---------- 伪装取消（超时类 OCE，ct 未取消）不得穿透降级信封 ----------

    [Fact(DisplayName = "海报兜底下载超时抛 TaskCanceledException（ct 未取消）→ 降级收 Warning，不伪装成取消判 Failed")]
    public async Task Poster_Timeout_FakeCancellation_DegradesTo_Warning_Not_Cancelled()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en", genres: "[]", posterPath: "/poster.jpg");
        // HttpClient 超时抛 TaskCanceledException（OperationCanceledException 子类），但 ct 并未取消（伪装取消）
        _poster.DownloadAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("模拟海报下载 30s 超时"));

        string videoPath = WriteFile("Inception.2010.1080p.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed, "伪装取消不应穿透为真取消把已落地的归档判 Failed");
        File.Exists(r.TargetPath).Should().BeTrue("视频已落地");
        File.Exists(videoPath).Should().BeFalse("源已移走——正因如此绝不能整条判 Failed");
        r.Warnings.Should().NotBeNullOrEmpty();
        r.Warnings!.Should().Contain(w => w.Contains("海报"));
        File.Exists(Path.ChangeExtension(r.TargetPath, ".nfo")).Should().BeTrue("nfo 不受海报失败影响照常写出");
    }

    [Fact(DisplayName = "海报下载抛真取消（ct 已取消）→ OperationCanceledException 照常上抛，不降级吞掉")]
    public async Task Poster_TrueCancellation_Propagates_Not_Degraded()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en", genres: "[]", posterPath: "/poster.jpg");
        using CancellationTokenSource cts = new();
        // 在海报下载这一刻才触发真取消：保证之前的 EF / 文件操作均以未取消状态走完
        _poster.DownloadAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<PosterDownloadResult>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        string videoPath = WriteFile("Inception.2010.1080p.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        Func<Task> act = () => _sut.ArchiveAsync(item, ArchiveOperation.Move, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("真取消必须透传给编排层，不能被降级信封吞掉");
    }

    // ---------- 字幕处理纳入落地后降级信封 ----------

    [Fact(DisplayName = "视频落地后字幕移动失败 → 不整条判 Failed：Outcome=Completed + Warnings 含字幕失败")]
    public async Task Subtitle_Failure_After_Video_Moved_DegradesTo_Warning_Not_Failed()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        string subPath = WriteFile("Foo.2020.CHS.srt", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        // 视频正常移动，字幕移动抛 IOException（模拟网络盘抖动 / 源目录被并发清理）
        ArchiveService sut = CreateSutWith(new SubtitleFailingFileMover(_fileMover));
        ArchiveResult r = await sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed, "视频已落地，字幕失败绝不能整条判 Failed");
        File.Exists(r.TargetPath).Should().BeTrue("视频已落地");
        File.Exists(videoPath).Should().BeFalse("源视频已移走");
        r.Warnings.Should().NotBeNullOrEmpty();
        r.Warnings!.Should().Contain(w => w.Contains("字幕处理失败"));
        File.Exists(subPath).Should().BeTrue("移动失败的字幕保留在源位，可人工补救");
    }

    [Fact(DisplayName = "字幕移动遇真取消（ct 已取消）→ OperationCanceledException 透传，不降级吞掉")]
    public async Task Subtitle_TrueCancellation_Propagates_Not_Degraded()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        WriteFile("Foo.2020.CHS.srt", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        using CancellationTokenSource cts = new();
        ArchiveService sut = CreateSutWith(new CancellingSubtitleFileMover(_fileMover, cts));

        Func<Task> act = () => sut.ArchiveAsync(item, ArchiveOperation.Move, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("字幕阶段的真取消必须透传给编排层");
    }

    [Fact]
    public async Task Copy_Mode_Lands_Target_And_Keeps_Source()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en",
                 genres: """[{"id":28,"name":"Action"}]""");
        string videoPath = WriteFile("Inception.2010.1080p.mkv", 2048);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item, ArchiveOperation.Copy);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        File.Exists(r.TargetPath).Should().BeTrue("Copy 已落地目标文件");
        File.Exists(videoPath).Should().BeTrue("Copy 模式保留源文件（NAS / 网盘场景）");
    }

    // ---------- 剧集 ----------

    [Fact]
    public async Task Tv_HappyPath_Moves_To_Season_Folder_And_Writes_TvShowAndEpisode_Nfo()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1396, mediaType: "tv", title: "绝命毒师", originalTitle: "Breaking Bad",
                 year: 2008, originCountry: ["US"], language: "en",
                 genres: """[{"id":18,"name":"Drama"}]""",
                 totalSeasons: 5);
        string videoPath = WriteFile("BreakingBad.S01E01.1080p.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1396, mediaType: "tv",
            parsed: new { title = "绝命毒师", year = 2008, type = "tv", season = 1, episode = 1 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().EndWith(Path.Combine("绝命毒师 (2008) {tmdb-1396}", "Season 01", "绝命毒师 (2008) - S01E01.mkv"));
        File.Exists(r.TargetPath).Should().BeTrue();

        string showRoot = Path.Combine(_targetRoot, "绝命毒师 (2008) {tmdb-1396}");
        File.Exists(Path.Combine(showRoot, "tvshow.nfo")).Should().BeTrue("剧集根 tvshow.nfo 必生成");
        File.Exists(Path.ChangeExtension(r.TargetPath, ".nfo")).Should().BeTrue("单集 nfo 必生成");
    }

    [Fact]
    public async Task Tv_Existing_TvShowNfo_Is_Not_Overwritten()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1396, mediaType: "tv", title: "绝命毒师", originalTitle: "Breaking Bad",
                 year: 2008, originCountry: ["US"], language: "en", genres: "[]", totalSeasons: 5);

        string showRoot = Path.Combine(_targetRoot, "绝命毒师 (2008) {tmdb-1396}");
        Directory.CreateDirectory(showRoot);
        string existingTvShowNfo = Path.Combine(showRoot, "tvshow.nfo");
        await File.WriteAllTextAsync(existingTvShowNfo, "<tvshow>existing</tvshow>");

        string videoPath = WriteFile("BB.S02E03.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1396, mediaType: "tv",
            parsed: new { title = "绝命毒师", year = 2008, type = "tv", season = 2, episode = 3 });

        await _sut.ArchiveAsync(item);

        (await File.ReadAllTextAsync(existingTvShowNfo)).Should().Be("<tvshow>existing</tvshow>",
            "已有 tvshow.nfo 不应被覆盖");
    }

    // ---------- 多季差异化：季文件夹季标题 + 季内文件名季年份 ----------

    [Fact(DisplayName = "多季：季文件夹带 TMDB 季名、集文件名用该季首播年、根目录维持整剧首播年")]
    public async Task Tv_MultiSeason_SeasonFolder_Has_Title_And_Episode_Uses_Season_Year()
    {
        long catId = SeedCategory(_targetRoot);
        // TMDB 缓存原始响应含多季 air_date + 篇章季名（鬼灭之刃：S1 2019 / S3 锻刀村篇 2023）
        SeedMeta(tmdbId: 85937, mediaType: "tv", title: "鬼灭之刃", originalTitle: "Demon Slayer",
                 year: 2019, originCountry: ["JP"], language: "ja", genres: "[]", totalSeasons: 3,
                 rawJson: """
                 {"seasons":[
                   {"season_number":1,"name":"第 1 季","air_date":"2019-04-06","episode_count":26},
                   {"season_number":3,"name":"锻刀村篇","air_date":"2023-04-09","episode_count":11}
                 ]}
                 """);
        string videoPath = WriteFile("Kimetsu.S03E01.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 85937, mediaType: "tv",
            parsed: new { title = "鬼灭之刃", year = 2019, type = "tv", season = 3, episode = 1 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        // 根目录用整剧首播年 2019；季文件夹带篇章名「锻刀村篇」；集文件名用该季首播年 2023
        r.TargetPath.Should().EndWith(
            Path.Combine("鬼灭之刃 (2019) {tmdb-85937}", "Season 03 锻刀村篇", "鬼灭之刃 (2023) - S03E01.mkv"));
        File.Exists(r.TargetPath).Should().BeTrue();
    }

    [Fact(DisplayName = "多季：TMDB 季名为默认名时季标题回退解析篇章名，季 air_date 缺失时集年份回退整剧年")]
    public async Task Tv_MultiSeason_FallsBack_To_ParsedSeasonTitle_And_ShowYear()
    {
        long catId = SeedCategory(_targetRoot);
        // S2 的 TMDB 季名是默认「第 2 季」且无 air_date → 季标题回退解析篇章名、集年份回退整剧年
        SeedMeta(tmdbId: 45782, mediaType: "tv", title: "刀剑神域", originalTitle: "Sword Art Online",
                 year: 2012, originCountry: ["JP"], language: "ja", genres: "[]", totalSeasons: 4,
                 rawJson: """{"seasons":[{"season_number":2,"name":"第 2 季","episode_count":24}]}""");
        string videoPath = WriteFile("SAO.S02E01.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 45782, mediaType: "tv",
            parsed: new { title = "刀剑神域", year = 2012, type = "tv", season = 2, episode = 1, seasonTitle = "幽灵子弹篇" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        // TMDB 季名「第 2 季」被过滤 → 回退解析篇章名「幽灵子弹篇」；无 air_date → 集年份回退整剧年 2012
        r.TargetPath.Should().EndWith(
            Path.Combine("刀剑神域 (2012) {tmdb-45782}", "Season 02 幽灵子弹篇", "刀剑神域 (2012) - S02E01.mkv"));
    }

    // ---------- 落点规范化：TMDB 规范标题 + {tmdb-NNN} 唯一性复用 ----------

    [Fact(DisplayName = "剧集：文件夹与文件标题段用 TMDB 规范名（解析出的篇章副标题被收敛）")]
    public async Task Tv_FolderName_Uses_Canonical_TmdbTitle_Not_ParsedTitle()
    {
        long catId = SeedCategory(_targetRoot);
        // TMDB 规范名「刀剑神域」；本次文件解析出的是带篇章副标题的「刀剑神域 Alicization」
        SeedMeta(tmdbId: 45782, mediaType: "tv", title: "刀剑神域", originalTitle: "Sword Art Online",
                 year: 2012, originCountry: ["JP"], language: "ja", genres: "[]", totalSeasons: 4);
        string videoPath = WriteFile("Sword.Art.Online.Alicization.S03E01.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 45782, mediaType: "tv",
            parsed: new { title = "刀剑神域 Alicization", year = 2012, type = "tv", season = 3, episode = 1 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().EndWith(
            Path.Combine("刀剑神域 (2012) {tmdb-45782}", "Season 03", "刀剑神域 (2012) - S03E01.mkv"),
            "文件夹与文件标题段一律用 TMDB 规范名，不跟随解析出的篇章副标题");
        r.TargetPath.Should().NotContain("Alicization");
    }

    [Fact(DisplayName = "电影：TMDB 中文名为空 → 标题段回退 TMDB 原文名（而非文件解析杂质名）")]
    public async Task Movie_EmptyChineseTitle_FallsBackTo_OriginalTitle_Not_ParsedName()
    {
        long catId = SeedCategory(_targetRoot);
        // 冷门作品 zh-CN 详情无中文译名（title 空串），但 TMDB 有权威原文 OriginalTitle
        SeedMeta(tmdbId: 99999, mediaType: "movie", title: "", originalTitle: "Frieren",
                 year: 2023, originCountry: ["JP"], language: "ja", genres: "[]");
        string videoPath = WriteFile("Sousou.no.Frieren.2023.WEB-DL.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 99999, mediaType: "movie",
            parsed: new { title = "Sousou no Frieren WEB-DL", year = 2023, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().EndWith(
            Path.Combine("Frieren (2023) {tmdb-99999}", "Frieren (2023) {tmdb-99999}.mkv"),
            "中文名为空时标题段用 TMDB 权威原文 OriginalTitle，而非文件解析杂质名");
        r.TargetPath.Should().NotContain("Sousou", "不应回退到文件解析名");
    }

    [Fact(DisplayName = "剧集：TMDB 中文名为空 → 文件夹与季内文件名段均回退 TMDB 原文名")]
    public async Task Tv_EmptyChineseTitle_FallsBackTo_OriginalTitle()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 88888, mediaType: "tv", title: "", originalTitle: "Frieren",
                 year: 2023, originCountry: ["JP"], language: "ja", genres: "[]", totalSeasons: 1);
        string videoPath = WriteFile("Sousou.no.Frieren.S01E01.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 88888, mediaType: "tv",
            parsed: new { title = "Sousou no Frieren", year = 2023, type = "tv", season = 1, episode = 1 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().EndWith(
            Path.Combine("Frieren (2023) {tmdb-88888}", "Season 01", "Frieren (2023) - S01E01.mkv"));
        r.TargetPath.Should().NotContain("Sousou");
    }

    [Fact(DisplayName = "剧集：分类根已存在同 tmdbId 标记目录（异名 / 大写 TMDB）→ 复用而非新建第二个")]
    public async Task Tv_ReusesExisting_TmdbFolder_EvenIf_TitleDiffers()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 45782, mediaType: "tv", title: "刀剑神域", originalTitle: "Sword Art Online",
                 year: 2012, originCountry: ["JP"], language: "ja", genres: "[]", totalSeasons: 4);
        // 历史散落：已存在一个异名但同 tmdbId 的目录（用大写 {TMDB-} 验证大小写不敏感匹配）
        string legacyFolder = Path.Combine(_targetRoot, "刀剑神域II (2012) {TMDB-45782}");
        Directory.CreateDirectory(legacyFolder);

        string videoPath = WriteFile("SAO.S02E05.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 45782, mediaType: "tv",
            parsed: new { title = "刀剑神域", year = 2012, type = "tv", season = 2, episode = 5 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().StartWith(legacyFolder + Path.DirectorySeparatorChar,
            "应落入已存在的同 tmdbId 目录，而非新建规范名目录");
        Directory.Exists(Path.Combine(_targetRoot, "刀剑神域 (2012) {tmdb-45782}"))
            .Should().BeFalse("不应再生成第二个文件夹");
    }

    [Fact(DisplayName = "电影：分类根已存在同 tmdbId 标记目录（异名）→ 复用，同一 tmdbId 仅一个文件夹")]
    public async Task Movie_ReusesExisting_TmdbFolder_EvenIf_TitleDiffers()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 120089, mediaType: "movie", title: "间谍过家家", originalTitle: "SPY×FAMILY",
                 year: 2022, originCountry: ["JP"], language: "ja", genres: "[]");
        string legacyFolder = Path.Combine(_targetRoot, "SPY×FAMILY (2022) {tmdb-120089}");
        Directory.CreateDirectory(legacyFolder);

        string videoPath = WriteFile("Spy.x.Family.2022.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 120089, mediaType: "movie",
            parsed: new { title = "Spy x Family", year = 2022, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().StartWith(legacyFolder + Path.DirectorySeparatorChar, "电影同样按 tmdbId 复用已存在目录");
        Directory.GetDirectories(_targetRoot).Should().ContainSingle("同一 tmdbId 只应有一个文件夹");
    }

    // ---------- 冲突 ----------

    [Fact]
    public async Task Conflict_TargetExists_Returns_ConflictSkipped_Source_Untouched()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");

        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllTextAsync(preExisting, "preexisting");

        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.ConflictSkipped);
        File.Exists(videoPath).Should().BeTrue("冲突时源文件不应被搬走");
        (await File.ReadAllTextAsync(preExisting)).Should().Be("preexisting", "目标内容不应被覆盖");
    }

    // ---------- 冲突策略：Overwrite / KeepBoth ----------

    [Fact(DisplayName = "冲突 Overwrite：新文件更大 → 替换已存在文件")]
    public async Task Conflict_Overwrite_LargerFile_Replaces()
    {
        SeedSetting("Archive_ConflictPolicy", "Overwrite");
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllBytesAsync(preExisting, new byte[100]);

        string videoPath = WriteFile("Foo.2020.mkv", 5000);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        new FileInfo(r.TargetPath).Length.Should().Be(5000, "更大的新文件已替换旧文件");
        File.Exists(videoPath).Should().BeFalse("源已移走");
    }

    [Fact(DisplayName = "冲突 Overwrite：新文件不大于已存在 → 跳过不替换")]
    public async Task Conflict_Overwrite_SmallerFile_Skips()
    {
        SeedSetting("Archive_ConflictPolicy", "Overwrite");
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllBytesAsync(preExisting, new byte[5000]);

        string videoPath = WriteFile("Foo.2020.mkv", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.ConflictSkipped);
        new FileInfo(preExisting).Length.Should().Be(5000, "更大的旧文件保留");
        File.Exists(videoPath).Should().BeTrue("源未动");
    }

    [Fact(DisplayName = "冲突 KeepBoth：加序号后缀保留两版本")]
    public async Task Conflict_KeepBoth_AddsSuffix()
    {
        SeedSetting("Archive_ConflictPolicy", "KeepBoth");
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllBytesAsync(preExisting, new byte[100]);

        string videoPath = WriteFile("Foo.2020.mkv", 200);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().EndWith("Foo (2020) {tmdb-1} (1).mkv");
        File.Exists(preExisting).Should().BeTrue("原文件保留");
        File.Exists(r.TargetPath).Should().BeTrue("新版本加后缀共存");
    }

    [Fact(DisplayName = "冲突 Overwrite：替换后旧 Completed 记录被失效（Skipped + 清 TargetPath），防撤销丢片 / 去重误判")]
    public async Task Conflict_Overwrite_Invalidates_Replaced_Completed_Records()
    {
        SeedSetting("Archive_ConflictPolicy", "Overwrite");
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllBytesAsync(preExisting, new byte[100]);
        // 旧记录：历史已完成归档、指向即将被替换的目标文件
        long replacedId = SeedCompletedItem(Path.Combine(_sourceDir, "old-foo.mkv"), preExisting, catId);
        // 对照记录：指向其它目标的已完成归档，不应被波及
        long unrelatedId = SeedCompletedItem(Path.Combine(_sourceDir, "other.mkv"), Path.Combine(movieFolder, "Other.mkv"), catId);

        string videoPath = WriteFile("Foo.2020.mkv", 5000);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem replaced = db.MediaItems.First(m => m.Id == replacedId);
        replaced.Status.Should().Be(MediaItemStatus.Skipped, "被替换记录退出 Completed，撤销 / 去重不再误用");
        replaced.TargetPath.Should().BeNull("被替换记录不得再指向新文件（否则撤销会把新文件搬走改名丢片）");
        replaced.ArchivedAt.Should().BeNull();
        // 时间线追加中文说明，供历史页溯源
        db.ProcessSteps.Any(s => s.MediaItemId == replacedId && s.Detail != null && s.Detail.Contains("新归档替换"))
            .Should().BeTrue("被替换记录的时间线应有中文替换说明");

        MediaItem unrelated = db.MediaItems.First(m => m.Id == unrelatedId);
        unrelated.Status.Should().Be(MediaItemStatus.Completed, "其它目标的已完成记录不受波及");
        unrelated.TargetPath.Should().NotBeNull();
    }

    [Fact(DisplayName = "冲突 Overwrite：替换时同步清理目标位旧字幕与旧 nfo，新字幕不再被旧文件顶掉")]
    public async Task Conflict_Overwrite_Removes_Stale_Companions_And_Lands_New_Subtitles()
    {
        SeedSetting("Archive_ConflictPolicy", "Overwrite");
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllBytesAsync(preExisting, new byte[100]);
        // 旧伴生文件：旧时间轴字幕 + 旧 nfo + 海报（海报不属同 stem 伴生，不应被清理）
        string oldSub = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.zh.srt");
        await File.WriteAllTextAsync(oldSub, "old-timeline-sub");
        string oldNfo = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.nfo");
        await File.WriteAllTextAsync(oldNfo, "<movie>old</movie>");
        string poster = Path.Combine(movieFolder, "poster.jpg");
        await File.WriteAllBytesAsync(poster, new byte[10]);

        string videoPath = WriteFile("Foo.2020.mkv", 5000);
        string newSub = WriteFile("Foo.2020.CHS.srt", 2000);
        await File.WriteAllTextAsync(newSub, "new-timeline-sub");
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        // 新字幕落位（旧 zh.srt 已被清理，不再同名冲突跳过）—— 修复前这里会留下旧时间轴字幕
        (await File.ReadAllTextAsync(Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.zh.srt")))
            .Should().Be("new-timeline-sub", "替换后新视频必须配新字幕，不能错配旧时间轴字幕");
        // nfo 重新生成（旧 nfo 已删，新 nfo 写出）
        string newNfo = await File.ReadAllTextAsync(oldNfo);
        newNfo.Should().NotContain(">old<", "旧 nfo 已被清理");
        newNfo.Should().Contain("<movie>", "新 nfo 已重新生成");
        File.Exists(poster).Should().BeTrue("poster.jpg 不属同 stem 伴生文件，不应被清理");
        r.Warnings.Should().BeNullOrEmpty("旧伴生清理后新字幕 / nfo 全部正常落位");
    }

    // ---------- 冲突策略：Ask（询问）/ ForceOverwrite（人工裁定覆盖） ----------

    [Fact(DisplayName = "冲突 Ask：返回 ConflictPending，不做任何文件操作（待人工裁定覆盖）")]
    public async Task Conflict_Ask_ReturnsPending_NoFileOp()
    {
        SeedSetting("Archive_ConflictPolicy", "Ask");
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllTextAsync(preExisting, "preexisting");

        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.ConflictPending);
        File.Exists(videoPath).Should().BeTrue("询问待确认时源文件不应被搬走");
        (await File.ReadAllTextAsync(preExisting)).Should().Be("preexisting", "待确认期间目标内容不应被覆盖");
    }

    [Fact(DisplayName = "ForceOverwrite：人工裁定无条件覆盖（系统策略 Skip 也照覆盖，新文件更小亦替换）")]
    public async Task ForceOverwrite_Replaces_Even_When_Smaller_And_Policy_Skip()
    {
        // 系统策略保持默认 Skip：证明 ForceOverwrite 调用参数优先于系统策略，且不比新旧大小
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string movieFolder = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}");
        Directory.CreateDirectory(movieFolder);
        string preExisting = Path.Combine(movieFolder, "Foo (2020) {tmdb-1}.mkv");
        await File.WriteAllBytesAsync(preExisting, new byte[5000]);

        string videoPath = WriteFile("Foo.2020.mkv", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item, ArchiveOperation.Move, ArchiveConflictResolution.ForceOverwrite);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        new FileInfo(r.TargetPath).Length.Should().Be(100, "人工裁定覆盖无视大小，较小的新文件也替换");
        File.Exists(videoPath).Should().BeFalse("源已移走");
    }

    // ---------- 磁盘空间检查 ----------

    [Fact(DisplayName = "磁盘空间不足 → 抛 BusinessException（由 ProcessFileService 转 Failed）")]
    public async Task InsufficientDiskSpace_Throws_And_Leaves_Source()
    {
        SeedSetting("Archive_MinFreeSpaceMB", "100000000"); // 要求 100TB 余量，必不足
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        Func<Task> act = async () => await _sut.ArchiveAsync(item);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*磁盘空间不足*");
        File.Exists(videoPath).Should().BeTrue("空间不足时源文件不应被搬走");
    }

    [Fact(DisplayName = "MinFreeSpaceMB 极端大值（long 上限）不溢出绕过预检 → 仍判空间不足")]
    public async Task MinFreeSpace_ExtremeValue_DoesNotOverflow_StillBlocks()
    {
        // 修复前：long.MaxValue * 1024 * 1024 溢出回绕成负数 → required < available 恒成立 → 预检形同虚设
        SeedSetting("Archive_MinFreeSpaceMB", long.MaxValue.ToString());
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        Func<Task> act = async () => await _sut.ArchiveAsync(item);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*磁盘空间不足*");
        File.Exists(videoPath).Should().BeTrue("预检拦截时源文件不应被搬走");
    }

    [Fact(DisplayName = "磁盘空间充足（默认余量 0）→ 正常归档")]
    public async Task SufficientDiskSpace_ArchivesNormally()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
    }

    [Fact(DisplayName = "源已在规范目标位（孤儿认领）→ 识别已就位、登记 Completed、不移动不删除")]
    public async Task SourceAlreadyAtCanonicalTarget_RegistersCompleted_WithoutMoving()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(1, "movie", "Foo", "Foo", 2020, ["US"], "en", "[]");
        // 把源文件直接放在规范目标位（= ArchiveAsync 会算出的落点）：模拟孤儿文件重新入库
        string canonical = Path.Combine(_targetRoot, "Foo (2020) {tmdb-1}", "Foo (2020) {tmdb-1}.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        await File.WriteAllBytesAsync(canonical, new byte[1024]);
        MediaItem item = await SeedMediaItemAtArchiving(canonical, catId, 1, "movie",
            new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        Path.GetFullPath(r.TargetPath).Should().Be(Path.GetFullPath(canonical));
        File.Exists(canonical).Should().BeTrue("源已在规范位，应原地登记、不移动也不删除");
    }

    // ---------- 字幕 ----------

    [Fact]
    public async Task Subtitles_Same_Stem_Moved_With_Plex_LanguageSuffix()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");

        string videoPath = WriteFile("Foo.2020.1080p.mkv", 1024);
        string subZh = WriteFile("Foo.2020.1080p.CHS.srt", 100);
        string subEn = WriteFile("Foo.2020.1080p.ENG.srt", 200);
        string unrelated = WriteFile("Bar.movie.srt", 50); // 不同 stem 不应被搬

        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);
        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        File.Exists(Path.Combine(targetDir, "Foo (2020) {tmdb-1}.zh.srt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "Foo (2020) {tmdb-1}.en.srt")).Should().BeTrue();
        File.Exists(unrelated).Should().BeTrue("不同 stem 的字幕不应被搬");
        File.Exists(subZh).Should().BeFalse();
        File.Exists(subEn).Should().BeFalse();
    }

    [Fact]
    public async Task Subtitles_Archive_Format_Is_Skipped_Not_Moved()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");

        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        string subZip = WriteFile("Foo.2020.subs.zip", 999);

        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        await _sut.ArchiveAsync(item);
        File.Exists(subZip).Should().BeTrue("zip 字幕包应被跳过不动");
    }

    [Fact(DisplayName = "字幕前缀边界：EP1 不抓走 EP11 的字幕，自身 . 与 - 分隔字幕正常抓")]
    public async Task Subtitles_Prefix_Boundary_EP1_DoesNot_Grab_EP11()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");

        string videoPath = WriteFile("EP1.mkv", 1024);
        string subOwn = WriteFile("EP1.CHS.srt", 100);          // . 边界 → 同源，应抓
        string subDash = WriteFile("EP1-CHT.srt", 80);          // - 边界 → 同源，应抓
        string subOther = WriteFile("EP11.CHS.srt", 120);       // 纯前缀过抓案例 → 不应抓

        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        File.Exists(Path.Combine(targetDir, "Foo (2020) {tmdb-1}.zh.srt")).Should().BeTrue("EP1 自身简中字幕应同步");
        File.Exists(Path.Combine(targetDir, "Foo (2020) {tmdb-1}.zh-TW.srt")).Should().BeTrue("- 分隔的繁中字幕应同步");
        File.Exists(subOwn).Should().BeFalse();
        File.Exists(subDash).Should().BeFalse();
        File.Exists(subOther).Should().BeTrue("EP11 的字幕属另一集，不得被 EP1 抓走");
    }

    [Fact(DisplayName = "字幕前缀边界：Movie 不抓走 Movie 2 的字幕（空格不算同源边界）")]
    public async Task Subtitles_Prefix_Boundary_Movie_DoesNot_Grab_Movie2()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");

        string videoPath = WriteFile("Movie.mkv", 1024);
        string subSequel = WriteFile("Movie 2.CHS.srt", 100); // 空格后是续作标题，不算同源

        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        File.Exists(subSequel).Should().BeTrue("Movie 2 的字幕属另一部作品，不得被 Movie 抓走");
        Directory.GetFiles(targetDir, "*.srt").Should().BeEmpty("没有同源字幕，目标位不应出现任何字幕");
    }

    [Fact(DisplayName = "剧集 Subs 子目录：季集匹配的字幕收编落地，邻集字幕留原地")]
    public async Task Tv_Subs_Subdir_SeasonEpisode_Match_Moved_Neighbor_Stays()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9101, mediaType: "tv", title: "Show", originalTitle: "Show", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]", totalSeasons: 1);
        string videoPath = WriteFile("Show.S01E02.mkv", 1024);
        string subOwn = WriteFileAt("Subs/Show.S01E02.chs.srt", 100);
        string subNeighbor = WriteFileAt("Subs/Show.S01E03.chs.srt", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9101, mediaType: "tv",
            parsed: new { title = "Show", year = 2020, type = "tv", season = 1, episode = 2 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        string expectedSub = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(r.TargetPath) + ".zh.srt");
        File.Exists(expectedSub).Should().BeTrue("Subs 子目录内 S01E02 与本集季集一致，应收编并按 Plex 命名带 .zh 后缀");
        File.Exists(subOwn).Should().BeFalse("Move 模式收编后源字幕应已移走");
        File.Exists(subNeighbor).Should().BeTrue("S01E03 属邻集，匹配不上必须留原地");
    }

    [Fact(DisplayName = "剧集组重排：编组内集号≠正典集号，非同名字幕与字幕子目录字幕按原始集号收编、重命名为正典 SxxEyy")]
    public async Task Tv_EpisodeGroup_Reorder_NonStemSubtitles_Match_By_OriginalEpisode_Renamed_Canonical()
    {
        // 高达SEED HD Remaster 剧集组：磁盘文件名为编组内第 2 集（EP02），TMDB 正典翻译为 S01E15。
        // 源视频 / 字幕文件名不变（仍带编组内原始集号 EP02），仅归档目标名用正典 S01E15。
        // 修复前：字幕归属用正典集号 15 匹配磁盘上的 EP02（=2）→ 落空，非同名字幕被遗留源目录。
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 20111, mediaType: "tv", title: "机动战士高达SEED", originalTitle: "Gundam SEED",
            year: 2002, originCountry: ["JP"], language: "ja", genres: "[]", totalSeasons: 1);

        string videoPath = WriteFile("Gundam.SEED.EP02.mkv", 1024);
        string sidecar = WriteFile("字幕组SEED.EP02.chs.srt", 200);   // 不同名 sidecar（stem≠视频）→ 走季集匹配
        string subdir = WriteFileAt("Subs/SEED.EP02.cht.srt", 150);   // 字幕子目录 → 走季集匹配
        string neighbor = WriteFile("字幕组SEED.EP03.chs.srt", 200);  // 编组内邻集（第 3 集）→ 必须留原地

        // ParsedInfo：正典 season=1 / episode=15（用于命名）；original*=源文件名命名空间集号 2（用于字幕匹配）
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 20111, mediaType: "tv",
            parsed: new { title = "机动战士高达SEED", year = 2002, type = "tv",
                          season = 1, episode = 15, originalSeason = 1, originalEpisode = 2 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.TargetPath.Should().Contain("S01E15", "归档命名用 TMDB 正典季集（翻译后），不用编组内原始集号");
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        string targetStem = Path.GetFileNameWithoutExtension(r.TargetPath);

        // 核心：非同名 sidecar 与字幕子目录字幕都按"原始集号 2"匹配上，并重命名为"正典 S01E15"的名字
        File.Exists(Path.Combine(targetDir, targetStem + ".zh.srt"))
            .Should().BeTrue("不同名 sidecar（EP02）应按原始集号收编，重命名为正典 S01E15 的 .zh 字幕");
        File.Exists(Path.Combine(targetDir, targetStem + ".zh-TW.srt"))
            .Should().BeTrue("字幕子目录里的 EP02 字幕应按原始集号收编，重命名为正典 S01E15 的 .zh-TW 字幕");
        File.Exists(sidecar).Should().BeFalse("Move 模式收编后源 sidecar 应已移走");
        File.Exists(subdir).Should().BeFalse("Move 模式收编后字幕子目录字幕应已移走");

        // 回归护栏：编组内第 3 集字幕属邻集，按原始集号匹配 [2,2] 落空 → 留原地（证明按集号精确匹配而非全抓）
        File.Exists(neighbor).Should().BeTrue("EP03 属编组内邻集，匹配不上必须留原地");
    }

    [Fact(DisplayName = "剧集 Subs 子目录：集号在目录段（Subs/EP02/chs.srt）合并提取后收编")]
    public async Task Tv_Subs_Subdir_Episode_In_DirectorySegment_Moved()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9102, mediaType: "tv", title: "Show", originalTitle: "Show", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]", totalSeasons: 1);
        string videoPath = WriteFile("Show.S01E02.mkv", 1024);
        string subOwn = WriteFileAt("Subs/EP02/chs.srt", 100);
        string subNeighbor = WriteFileAt("Subs/EP03/chs.srt", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9102, mediaType: "tv",
            parsed: new { title = "Show", year = 2020, type = "tv", season = 1, episode = 2 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        string expectedSub = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(r.TargetPath) + ".zh.srt");
        File.Exists(expectedSub).Should().BeTrue("集号在目录段 EP02、语言在文件名 chs，目录段与文件名合并提取后应收编");
        File.Exists(subOwn).Should().BeFalse("收编后源字幕应已移走");
        File.Exists(subNeighbor).Should().BeTrue("EP03 目录段属邻集，必须留原地");
    }

    [Fact(DisplayName = "剧集 Subs 子目录：文件名识别不出语言时回退目录段（Subs/简体 → .zh）")]
    public async Task Tv_Subs_Subdir_Language_From_DirectorySegment_Fallback()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9103, mediaType: "tv", title: "Show", originalTitle: "Show", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]", totalSeasons: 1);
        string videoPath = WriteFile("Show.S01E02.mkv", 1024);
        string sub = WriteFileAt("Subs/简体/Show.S01E02.srt", 100); // 文件名无语言 token
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9103, mediaType: "tv",
            parsed: new { title = "Show", year = 2020, type = "tv", season = 1, episode = 2 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        string expectedSub = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(r.TargetPath) + ".zh.srt");
        File.Exists(expectedSub).Should().BeTrue("文件名识别不出语言时应回退用 PathHint 目录段「简体」识别为 zh");
        File.Exists(sub).Should().BeFalse("收编后源字幕应已移走");
    }

    [Fact(DisplayName = "剧集顶层不同名字幕：季集一致即收编，邻集留原地")]
    public async Task Tv_TopLevel_DifferentStem_SeasonEpisode_Match_Moved()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9104, mediaType: "tv", title: "Show", originalTitle: "Show", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]", totalSeasons: 1);
        string videoPath = WriteFile("[ANi] Show - 01.mkv", 1024);
        string subOwn = WriteFile("Frieren.S01E01.chs.srt", 100);      // stem 不同名，但季集与本集一致
        string subNeighbor = WriteFile("Frieren.S01E02.chs.srt", 100); // 邻集
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9104, mediaType: "tv",
            parsed: new { title = "Show", year = 2020, type = "tv", season = 1, episode = 1 });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        string expectedSub = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(r.TargetPath) + ".zh.srt");
        File.Exists(expectedSub).Should().BeTrue("顶层不同名字幕 S01E01 与本集季集一致，剧集应收编");
        File.Exists(subOwn).Should().BeFalse("收编后源字幕应已移走");
        File.Exists(subNeighbor).Should().BeTrue("S01E02 属邻集，不得被收编");
    }

    [Fact(DisplayName = "电影 Subs 子目录：无季集标记的字幕按语言双双收编落地")]
    public async Task Movie_Subs_Subdir_Unmarked_Subtitles_Moved_With_Language()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9105, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        string subZh = WriteFileAt("Subs/chs.srt", 100);
        string subEn = WriteFileAt("Subs/eng.srt", 200);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9105, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        string stem = Path.GetFileNameWithoutExtension(r.TargetPath);
        File.Exists(Path.Combine(targetDir, stem + ".zh.srt")).Should().BeTrue("Subs/chs.srt 无季集标记且源树内唯一视频，应收编为 .zh");
        File.Exists(Path.Combine(targetDir, stem + ".en.srt")).Should().BeTrue("Subs/eng.srt 同理应收编为 .en");
        File.Exists(subZh).Should().BeFalse("收编后源字幕应已移走");
        File.Exists(subEn).Should().BeFalse("收编后源字幕应已移走");
    }

    [Fact(DisplayName = "电影 Subs 守门：源树内另有视频时不收编（防平铺多电影互抢）")]
    public async Task Movie_Subs_Gate_OtherVideoInTree_Subtitle_Stays()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9106, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        WriteFile("Other.mkv", 512); // 源树内第二个视频 → 守门关闭电影收编
        string sub = WriteFileAt("Subs/chs.srt", 100);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9106, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        File.Exists(sub).Should().BeTrue("源树内另有 Other.mkv，字幕归属不明确，必须留原地");
        Directory.GetFiles(targetDir, "*.srt").Should().BeEmpty("守门关闭收编时目标位不应出现任何字幕");
    }

    [Fact(DisplayName = "电影 Subs 子目录：带季集标记的剧集字幕不收编")]
    public async Task Movie_Subs_Subdir_EpisodeMarked_Subtitle_Stays()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 9107, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        string sub = WriteFileAt("Subs/Show.S01E01.srt", 100); // 季集标记 = 剧集字幕
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 9107, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        string targetDir = Path.GetDirectoryName(r.TargetPath)!;
        File.Exists(sub).Should().BeTrue("带 S01E01 季集标记的字幕属剧集，电影不得收编");
        Directory.GetFiles(targetDir, "*.srt").Should().BeEmpty("剧集字幕被拒后目标位不应出现任何字幕");
    }

    // ---------- Webhook ----------

    [Fact]
    public async Task Webhook_SubscribedTo_MediaArchived_GetsDelivery_AndEnqueued()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        long subId = SeedWebhookSub(name: "sub-A", events: ["media.archived"], enabled: true);
        long unrelatedSub = SeedWebhookSub(name: "sub-B", events: ["media.failed"], enabled: true);
        long disabledSub = SeedWebhookSub(name: "sub-C", events: ["media.archived"], enabled: false);
        SetWebhookEnabled(true); // 总开关种子默认 false；本用例验证「开启时」投递写入与入队

        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        await _sut.ArchiveAsync(item);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        List<WebhookDelivery> deliveries = db.WebhookDeliveries.ToList();
        deliveries.Should().ContainSingle(d => d.SubscriptionId == subId, "只有订阅 media.archived 的启用订阅入 Delivery");
        deliveries.Should().NotContain(d => d.SubscriptionId == unrelatedSub, "未订阅 media.archived 的不写");
        deliveries.Should().NotContain(d => d.SubscriptionId == disabledSub, "Enabled=false 不写");

        _outboxQueue.Enqueued.Should().ContainSingle();
        deliveries[0].Status.Should().Be(WebhookDeliveryStatus.Pending);
        deliveries[0].Event.Should().Be("media.archived");
        deliveries[0].Payload.Should().Contain("\"tmdbId\":1").And.Contain("\"type\":\"movie\"").And.Contain("\"title\":\"Foo\"");
    }

    [Fact(DisplayName = "media.archived payload：全部成功时 metadataPending=false + warnings 空数组")]
    public async Task Webhook_Payload_MetadataPending_False_When_Fully_Successful()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        SeedWebhookSub(name: "sub-A", events: ["media.archived"], enabled: true);
        SetWebhookEnabled(true);

        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        await _sut.ArchiveAsync(item);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        string payload = db.WebhookDeliveries.Single().Payload;
        payload.Should().Contain("\"metadataPending\":false");
        payload.Should().Contain("\"warnings\":[]");
    }

    [Fact(DisplayName = "media.archived payload：降级归档时 metadataPending=true + warnings 明细，订阅端可感知元数据不完整")]
    public async Task Webhook_Payload_MetadataPending_True_With_Warnings_On_Degraded_Archive()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 27205, mediaType: "movie", title: "盗梦空间", originalTitle: "Inception",
                 year: 2010, originCountry: ["US"], language: "en", genres: "[]");
        SeedWebhookSub(name: "sub-A", events: ["media.archived"], enabled: true);
        SetWebhookEnabled(true);

        // 注入 nfo 写入失败（目标 nfo 路径被同名目录占用），制造「视频落地 + 元数据待补」降级归档
        string showFolder = Path.Combine(_targetRoot, "盗梦空间 (2010) {tmdb-27205}");
        Directory.CreateDirectory(Path.Combine(showFolder, "盗梦空间 (2010) {tmdb-27205}.nfo"));

        string videoPath = WriteFile("Inception.2010.1080p.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 27205, mediaType: "movie",
            parsed: new { title = "盗梦空间", year = 2010, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed);
        r.Warnings.Should().NotBeNullOrEmpty();
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string payload = db.WebhookDeliveries.Single().Payload;
        payload.Should().Contain("\"metadataPending\":true", "订阅端（如 Plex 刷库自动化）须能识别归档元数据不完整");
        payload.Should().Contain("\"warnings\":[\"", "warnings 数组应携带具体失败明细");
    }

    [Fact]
    public async Task Webhook_NoSubscriptions_NoDeliveries_NoEnqueue()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        await _sut.ArchiveAsync(item);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookDeliveries.Count().Should().Be(0);
        _outboxQueue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_GloballyDisabled_NoDeliveries_EvenWithEnabledSub()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        // 总开关种子默认 false；即便有启用且订阅 media.archived 的订阅，也不应产生任何投递
        SeedWebhookSub(name: "sub-A", events: ["media.archived"], enabled: true);

        string videoPath = WriteFile("Foo.2020.mkv", 1024);
        MediaItem item = await SeedMediaItemAtArchiving(videoPath, catId, tmdbId: 1, mediaType: "movie",
            parsed: new { title = "Foo", year = 2020, type = "movie" });

        ArchiveResult r = await _sut.ArchiveAsync(item);

        r.Outcome.Should().Be(ArchiveOutcome.Completed); // 归档主流程不受总开关影响
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookDeliveries.Count().Should().Be(0, "总开关关闭时不产生任何 Webhook_Delivery");
        _outboxQueue.Enqueued.Should().BeEmpty();
    }

    // ---------- 前置校验 ----------

    [Theory]
    [InlineData(null, "movie", "{}", "缺 CategoryId")]
    public async Task Missing_CategoryId_Throws(long? catId, string mediaType, string parsedInfo, string _)
    {
        MediaItem item = NewItemRaw(WriteFile("x.mkv", 10), categoryId: catId, tmdbId: 1, mediaType: mediaType, parsedInfo: parsedInfo);
        Func<Task> act = async () => await _sut.ArchiveAsync(item);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*CategoryId*");
    }

    [Fact]
    public async Task Missing_TmdbId_Throws()
    {
        long catId = SeedCategory(_targetRoot);
        MediaItem item = NewItemRaw(WriteFile("x.mkv", 10), categoryId: catId, tmdbId: null, mediaType: "movie", parsedInfo: "{}");
        Func<Task> act = async () => await _sut.ArchiveAsync(item);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*TmdbId*");
    }

    [Fact]
    public async Task Missing_ParsedInfo_Throws()
    {
        long catId = SeedCategory(_targetRoot);
        MediaItem item = NewItemRaw(WriteFile("x.mkv", 10), categoryId: catId, tmdbId: 1, mediaType: "movie", parsedInfo: null);
        Func<Task> act = async () => await _sut.ArchiveAsync(item);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*ParsedInfo*");
    }

    [Fact]
    public async Task ParsedInfo_Missing_Year_Throws_Sensible_Error()
    {
        long catId = SeedCategory(_targetRoot);
        SeedMeta(tmdbId: 1, mediaType: "movie", title: "Foo", originalTitle: "Foo", year: 2020,
            originCountry: ["US"], language: "en", genres: "[]");
        MediaItem item = NewItemRaw(WriteFile("x.mkv", 10), categoryId: catId, tmdbId: 1, mediaType: "movie",
            parsedInfo: """{"title":"Foo","type":"movie"}""");
        Func<Task> act = async () => await _sut.ArchiveAsync(item);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*year*");
    }

    // ---------- helpers ----------

    private string WriteFile(string fileName, long size)
    {
        string path = Path.Combine(_sourceDir, fileName);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    /// <summary>按相对路径写文件到源目录（自动建出中间子目录，用于 Subs 等字幕子目录场景）</summary>
    private string WriteFileAt(string relativePath, long size)
    {
        string path = Path.Combine(_sourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private void SeedSetting(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Archive" });
        db.SaveChanges();
    }

    private long SeedCategory(string targetRoot)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new()
        {
            Name = $"Cat-{Guid.NewGuid():N}",
            MediaType = MediaType.Both,
            TargetRoot = targetRoot,
            Priority = 100,
        };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    private void SeedMeta(int tmdbId, string mediaType, string title, string originalTitle, int year,
        string[] originCountry, string language, string genres, string? plot = null, int? totalSeasons = null,
        string? rawJson = null, string? posterPath = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        TmdbMetadataCache m = new()
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            OriginalTitle = originalTitle,
            Year = year,
            TotalSeasons = totalSeasons,
            OriginCountry = originCountry.ToList(),
            OriginalLanguage = language,
            Genres = genres,
            Overview = plot,
            RawJson = rawJson ?? "{}",
            PosterPath = posterPath,
            CachedAt = DateTimeOffset.UtcNow,
        };
        db.TmdbMetadataCaches.Add(m);
        db.SaveChanges();
    }

    /// <summary>种子一条「已完成归档」记录（指向给定 TargetPath），模拟历史归档结果</summary>
    private long SeedCompletedItem(string sourcePath, string targetPath, long categoryId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(
            sourcePath, Path.GetFileName(sourcePath), fileSize: 100,
            status: MediaItemStatus.Completed,
            tmdbId: 1,
            tmdbMediaType: "movie",
            categoryId: categoryId,
            parsedInfo: """{"title":"Foo","year":2020,"type":"movie"}""",
            targetPath: targetPath);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>覆写 Webhook 总开关（EnsureCreated 已种子 Webhook_Enabled=false）</summary>
    private void SetWebhookEnabled(bool enabled)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? row = db.SystemSettings.FirstOrDefault(s => s.Key == "Webhook_Enabled");
        if (row is null)
            db.SystemSettings.Add(new SystemSetting { Key = "Webhook_Enabled", Value = enabled ? "true" : "false", Category = "Webhook" });
        else
            row.Value = enabled ? "true" : "false";
        db.SaveChanges();
    }

    private long SeedWebhookSub(string name, string[] events, bool enabled)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        WebhookSubscription s = new()
        {
            Name = name,
            Url = $"https://hook.test/{name}",
            Events = events.ToList(),
            Enabled = enabled,
            TimeoutSeconds = 10,
        };
        db.WebhookSubscriptions.Add(s);
        db.SaveChanges();
        return s.Id;
    }

    private MediaItem NewItemRaw(string sourcePath, long? categoryId, int? tmdbId, string? mediaType, string? parsedInfo)
    {
        return MediaItem.CreateFixture(
            sourcePath, Path.GetFileName(sourcePath), new FileInfo(sourcePath).Length,
            categoryId: categoryId,
            tmdbId: tmdbId,
            tmdbMediaType: mediaType,
            parsedInfo: parsedInfo);
    }

    private async Task<MediaItem> SeedMediaItemAtArchiving(string sourcePath, long categoryId, int tmdbId, string mediaType, object parsed)
    {
        await Task.Yield();
        using PmmDbContext db = _dbFactory.CreateDbContext();
        // fixture 直接构造到 Archiving 状态，避免逐步 Transition + 反复 set 字段
        MediaItem item = MediaItem.CreateFixture(
            sourcePath, Path.GetFileName(sourcePath), new FileInfo(sourcePath).Length,
            status: MediaItemStatus.Archiving,
            tmdbId: tmdbId,
            tmdbMediaType: mediaType,
            categoryId: categoryId,
            parseSource: ParseSource.Rule,
            confidence: 0.9,
            parsedInfo: JsonSerializer.Serialize(parsed));
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item;
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

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeWebhookOutboxQueue : IWebhookOutboxQueue
    {
        public List<long> Enqueued { get; } = new();
        public ValueTask EnqueueAsync(long deliveryId, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(deliveryId);
            return ValueTask.CompletedTask;
        }
        public System.Threading.Channels.ChannelReader<long> Reader => throw new NotImplementedException();
    }

    /// <summary>视频正常移动、字幕（.srt）移动抛 IOException 的注入器 —— 模拟网络盘抖动只打中字幕阶段</summary>
    private sealed class SubtitleFailingFileMover : IFileMover
    {
        private readonly IFileMover _inner;
        public SubtitleFailingFileMover(IFileMover inner) => _inner = inner;

        public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default)
            => targetPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("模拟网络盘抖动：字幕移动失败")
                : _inner.MoveAsync(sourcePath, targetPath, ct);

        public Task CopyAsync(string sourcePath, string targetPath, CancellationToken ct = default)
            => _inner.CopyAsync(sourcePath, targetPath, ct);
    }

    /// <summary>视频正常移动、移动字幕（.srt）那一刻触发真取消并抛 OCE 的注入器</summary>
    private sealed class CancellingSubtitleFileMover : IFileMover
    {
        private readonly IFileMover _inner;
        private readonly CancellationTokenSource _cts;
        public CancellingSubtitleFileMover(IFileMover inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default)
        {
            if (targetPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            {
                _cts.Cancel();
                throw new OperationCanceledException(_cts.Token);
            }
            return _inner.MoveAsync(sourcePath, targetPath, ct);
        }

        public Task CopyAsync(string sourcePath, string targetPath, CancellationToken ct = default)
            => _inner.CopyAsync(sourcePath, targetPath, ct);
    }
}
