using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Review;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D7.5 ReviewService — list / confirm / ignore / batch / tmdb-search / bind-tmdb 全分支</summary>
public sealed class ReviewServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ITmdbSearchService _tmdb;
    private readonly IArchiveService _archive;
    private readonly IFileProbe _fileProbe;
    private readonly TestFolderSeriesCache _folderCache;
    private readonly IWebhookEmitter _webhook;
    private readonly ReviewService _sut;
    private readonly string _scratch;

    public ReviewServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _tmdb = Substitute.For<ITmdbSearchService>();
        _archive = Substitute.For<IArchiveService>();
        _fileProbe = Substitute.For<IFileProbe>();
        _folderCache = new TestFolderSeriesCache();
        _webhook = Substitute.For<IWebhookEmitter>();
        _sut = new ReviewService(_dbFactory, _tmdb, _archive, _fileProbe, _folderCache, _webhook, NullLogger<ReviewService>.Instance);

        _scratch = Path.Combine(Path.GetTempPath(), $"pmm-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ---------- List ----------

    [Fact]
    public async Task List_Returns_Only_AwaitingReview_Items()
    {
        long inReview1 = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long inReview2 = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule);
        SeedItem(MediaItemStatus.Completed, ParseSource.Rule);
        SeedItem(MediaItemStatus.Queued, ParseSource.Rule);

        ReviewListPage page = await _sut.ListAsync(new ReviewListQuery());

        page.Total.Should().Be(2);
        page.Items.Should().HaveCount(2);
        page.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { inReview1, inReview2 });
    }

    [Fact]
    public async Task List_Filter_By_ParseSource()
    {
        SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long ruleId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule);

        ReviewListPage page = await _sut.ListAsync(new ReviewListQuery(ParseSource: ParseSource.Rule));

        page.Items.Should().ContainSingle(i => i.Id == ruleId);
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task List_Pagination_Skip_And_Take_Apply()
    {
        for (int i = 0; i < 5; i++) SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);

        ReviewListPage p1 = await _sut.ListAsync(new ReviewListQuery(Page: 1, PageSize: 2));
        ReviewListPage p2 = await _sut.ListAsync(new ReviewListQuery(Page: 2, PageSize: 2));

        p1.Items.Should().HaveCount(2);
        p2.Items.Should().HaveCount(2);
        p1.Items.Select(i => i.Id).Should().NotIntersectWith(p2.Items.Select(i => i.Id));
        p1.Total.Should().Be(5);
    }

    [Fact]
    public async Task List_Includes_Tmdb_Candidates_From_Cache()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai, tmdbId: 27205, mediaType: "movie");
        SeedTmdbCache(27205, "movie", "盗梦空间", "Inception", 2010, "/poster.jpg");

        ReviewListPage page = await _sut.ListAsync(new ReviewListQuery());

        ReviewItemResponse item = page.Items.Single(i => i.Id == itemId);
        item.TmdbCandidates.Should().ContainSingle();
        item.TmdbCandidates[0].Title.Should().Be("盗梦空间");
        item.TmdbCandidates[0].PosterUrl.Should().Contain("/poster.jpg");
    }

    [Fact]
    public async Task List_Includes_All_Candidates_From_Snapshot_Json()
    {
        // 解析阶段持久化的多候选快照 → 审核列表应展示全部候选（而非仅当前绑定的 1 个）
        const string snapshot = """[{"tmdbId":11,"mediaType":"movie","title":"片A","year":2001,"posterPath":"/a.jpg"},{"tmdbId":22,"mediaType":"movie","title":"片B","year":2002,"posterPath":"/b.jpg"}]""";
        long itemId = SeedItemWithCandidates(snapshot);

        ReviewListPage page = await _sut.ListAsync(new ReviewListQuery());

        ReviewItemResponse item = page.Items.Single(i => i.Id == itemId);
        item.TmdbCandidates.Should().HaveCount(2);
        item.TmdbCandidates.Select(c => c.TmdbId).Should().BeEquivalentTo(new[] { 11, 22 });
        item.TmdbCandidates[0].PosterUrl.Should().Contain("/a.jpg");
    }

    [Fact]
    public async Task List_Backfills_TotalSeasons_For_Snapshot_Candidate_From_Cache()
    {
        // 快照候选本身不含季数：已缓存详情的候选（默认绑定的 top）应从 TmdbMetadataCache 回填 TotalSeasons，
        // 让审核页季号下拉就绪；未命中缓存的候选仍为 null（前端选中时再懒查 tmdb-detail）。
        const string snapshot = """[{"tmdbId":11,"mediaType":"tv","title":"剧A","year":2001,"posterPath":"/a.jpg"},{"tmdbId":22,"mediaType":"tv","title":"剧B","year":2002,"posterPath":"/b.jpg"}]""";
        long itemId = SeedItemWithCandidates(snapshot, tmdbId: 11, mediaType: "tv");
        SeedTmdbCache(11, "tv", "剧A", "ShowA", 2001, "/a.jpg", totalSeasons: 3);

        ReviewListPage page = await _sut.ListAsync(new ReviewListQuery());

        ReviewItemResponse item = page.Items.Single(i => i.Id == itemId);
        item.TmdbCandidates.Single(c => c.TmdbId == 11).TotalSeasons.Should().Be(3);
        item.TmdbCandidates.Single(c => c.TmdbId == 22).TotalSeasons.Should().BeNull();
    }

    // ---------- Confirm ----------

    [Fact]
    public async Task Confirm_Movie_Transitions_To_Archiving_And_Calls_ArchiveService()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010)/Inception (2010).mkv", ArchiveOutcome.Completed));

        ConfirmResult r = await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        r.Status.Should().Be(MediaItemStatus.Archiving);
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());

        MediaItem final = ReadItem(itemId);
        final.Status.Should().Be(MediaItemStatus.Completed, "ArchiveService 成功后 Transition Completed");
        final.TmdbId.Should().Be(27205);
        final.CategoryId.Should().Be(catId);
    }

    [Fact]
    public async Task Confirm_Tv_Without_Season_Or_Episode_Throws()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(1, "tv", catId, "Show", 2020, Season: null, Episode: 1, rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*剧集*季号*集号*");
    }

    [Fact]
    public async Task Confirm_Tv_MissingSeason_SingleSeason_AutoFills_And_Archives()
    {
        // 剧集确认时缺季号但集号在、TMDB 仅 1 季 → 后端自动补 S01 并归档（前端可不填季号）
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "JJK", "呪術廻戦", 2020, 1, null, ["JP"], "ja", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Tv/JJK/S01E59.mkv", ArchiveOutcome.Completed));

        ConfirmResult r = await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(95479, "tv", catId, "JJK", 2020, Season: null, Episode: 59, rv));

        r.Status.Should().Be(MediaItemStatus.Archiving);
        MediaItem final = ReadItem(itemId);
        final.Status.Should().Be(MediaItemStatus.Completed);
        final.ParsedInfo.Should().Contain("\"season\":1");
    }

    [Fact]
    public async Task Confirm_RowVersion_Mismatch_Throws_Concurrency_Message()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(1, "movie", catId, null, 2020, null, null, RowVersion: 9999));

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*已被其他用户修改*");
    }

    [Fact]
    public async Task Confirm_NonExisting_Id_Throws_NotFound()
    {
        long catId = SeedCategory();
        Func<Task> act = async () => await _sut.ConfirmAsync(99999,
            new ConfirmRequest(1, "movie", catId, null, 2020, null, null, 0));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("记录不存在");
    }

    [Fact]
    public async Task Confirm_NotInAwaitingReview_Throws()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.Completed, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(1, "movie", catId, null, 2020, null, null, rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*待确认状态*");
    }

    [Fact]
    public async Task Confirm_Missing_Category_Throws()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(1, "movie", "T", null, 2020, null, null, null, null, null, null, "{}"));

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(1, "movie", CategoryId: 9999, null, 2020, null, null, rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("分类不存在");
    }

    [Fact]
    public async Task Confirm_TmdbClientException_Maps_To_InvalidTmdbId_BusinessError()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new TmdbClientException("404 not found", 404));

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(99999999, "movie", catId, null, 2020, null, null, rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*TMDB ID 无效*");
    }

    [Fact]
    public async Task Confirm_ArchiveException_MarksFailed_And_Throws_9000_Style()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, null, null, null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("disk full"));

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*提交归档任务失败*");

        ReadItem(itemId).Status.Should().Be(MediaItemStatus.Failed);
    }

    // ---------- Confirm：归档时间线（与自动 / 手动入口同口径） ----------

    [Fact(DisplayName = "确认归档成功：时间线落 Archiving（operation=MOVE）+ Completed 终态步骤")]
    public async Task Confirm_Success_Appends_Archiving_And_Terminal_Timeline_Steps()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010)/Inception (2010).mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        List<ProcessStep> steps = ReadSteps(itemId);
        ProcessStep archiving = steps.Should().ContainSingle(s => s.Stage == MediaItemStatus.Archiving,
            "确认归档不落 Archiving 步骤会让时间线断档、撤销归档的 Move/Copy 判定依赖巧合").Which;
        archiving.Detail.Should().Contain("\"operation\":\"MOVE\"")
            .And.Contain("Inception (2010).mkv")
            .And.NotContain("metadataPending", "无警告时不落待补元数据字段");
        steps.Should().ContainSingle(s => s.Stage == MediaItemStatus.Completed, "终态也要有时间线收尾步骤");
    }

    [Fact(DisplayName = "确认归档带 Warnings：时间线 Archiving 步骤标记 metadataPending + 警告明细，仍判 Completed")]
    public async Task Confirm_Archive_Warnings_Recorded_As_MetadataPending_In_Timeline()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010)/Inception (2010).mkv", ArchiveOutcome.Completed,
                new[] { "nfo 写入失败：磁盘只读" }));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        ReadItem(itemId).Status.Should().Be(MediaItemStatus.Completed, "视频已落地，元数据失败仅降级警告不回退");
        ProcessStep archiving = ReadSteps(itemId).Single(s => s.Stage == MediaItemStatus.Archiving);
        archiving.Detail.Should().Contain("\"metadataPending\":true", "降级警告必须在时间线可见，不能被静默丢弃")
            .And.Contain("nfo 写入失败");
    }

    // ---------- Confirm：中断不卡 Archiving（失败补偿用 CancellationToken.None） ----------

    [Fact(DisplayName = "归档中用户中断（ct 取消）：记录收尾为可恢复终态 Failed，绝不停留在 Archiving")]
    public async Task Confirm_Cancelled_During_Archive_Lands_On_Failed_Not_Archiving()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        using CancellationTokenSource cts = new();
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Throws(_ =>
            {
                cts.Cancel(); // 模拟归档进行中用户中断（请求取消 / 关停）
                return new OperationCanceledException(cts.Token);
            });

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("真取消透传，不包装为业务错");
        MediaItem final = ReadItem(itemId);
        final.Status.Should().Be(MediaItemStatus.Failed,
            "失败补偿落库必须用 CancellationToken.None：沿用已取消的 ct 会让补偿自身抛取消、记录永卡 Archiving");
        final.ErrorMessage.Should().Contain("中断");
        ReadSteps(itemId).Should().ContainSingle(s => s.Stage == MediaItemStatus.Failed, "失败收尾也落时间线");
    }

    [Fact(DisplayName = "批量确认中途取消：当前条收尾 Failed、其余保持 AwaitingReview，取消透传")]
    public async Task BatchConfirm_Cancelled_MidBatch_Propagates_And_Leaves_No_Archiving()
    {
        long catId = SeedCategory();
        long a = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long b = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule);
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(1, "movie", "T", null, 2020, null, null, null, null, null, null, "{}"));
        using CancellationTokenSource cts = new();
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Throws(_ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });

        Func<Task> act = async () => await _sut.BatchConfirmAsync(new BatchConfirmRequest(new[]
        {
            new BatchConfirmItem(a, 1, "movie", catId, null, 2020, null, null, ReadItem(a).RowVersion),
            new BatchConfirmItem(b, 1, "movie", catId, null, 2020, null, null, ReadItem(b).RowVersion),
        }), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ReadItem(a).Status.Should().Be(MediaItemStatus.Failed, "归档中被中断的当前条须收尾为 Failed");
        ReadItem(b).Status.Should().Be(MediaItemStatus.AwaitingReview, "未处理条保持原状，不受整批取消影响");
    }

    // ---------- Confirm：EpisodeEnd 表单权威 + movie 清残留季集 ----------

    [Fact(DisplayName = "清空末集：确认表单 EpisodeEnd=null 显式清除解析残留区间（不被 MergeWith 静默保留）")]
    public async Task Confirm_Cleared_EpisodeEnd_Is_Form_Authoritative()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai,
            parsedInfo: """{"title":"Show","year":2020,"type":"tv","season":1,"episode":8,"episodeEnd":9,"matchedRuleId":null}""");
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "Show", null, 2020, 2, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Tv/Show/S01E08.mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(95479, "tv", catId, "Show", 2020, Season: 1, Episode: 8, rv, EpisodeEnd: null));

        string parsed = ReadItem(itemId).ParsedInfo!;
        parsed.Should().Contain("\"episodeEnd\":null", "用户清空末集后实际归档不得带回 stale 区间")
            .And.NotContain("\"episodeEnd\":9");
    }

    [Fact(DisplayName = "电影确认：顺带清除解析残留的 season / episode / episodeEnd")]
    public async Task Confirm_Movie_Clears_Residual_Season_Episode_Fields()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai,
            parsedInfo: """{"title":"X","year":2020,"type":"tv","season":1,"episode":2,"episodeEnd":3,"matchedRuleId":null}""");
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010).mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        string parsed = ReadItem(itemId).ParsedInfo!;
        parsed.Should().Contain("\"type\":\"movie\"")
            .And.Contain("\"season\":null").And.Contain("\"episode\":null").And.Contain("\"episodeEnd\":null");
    }

    [Fact(DisplayName = "预览与确认口径一致：表单清空末集 → 预览无区间，确认落库的 ParsedInfo 同样无区间")]
    public async Task Preview_And_Confirm_Agree_When_EpisodeEnd_Cleared()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai,
            parsedInfo: """{"title":"Show","year":2020,"type":"tv","season":1,"episode":8,"episodeEnd":9,"matchedRuleId":null}""");
        long rv = ReadItem(itemId).RowVersion;

        // 预览直接用表单值（EpisodeEnd=null）→ 路径无 -E09 区间
        ReviewPreviewPathResult preview = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 95479, "tv", "Show", 2020, 1, 8, null, catId, "x.mkv"),
        }));
        preview.Entries.Single().RelativePath.Should().Contain("S01E08").And.NotContain("-E09");

        // 同一表单值确认 → 实际归档采用的 ParsedInfo 同样无区间（预览 = 实际落点）
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "Show", null, 2020, 2, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Tv/Show/S01E08.mkv", ArchiveOutcome.Completed));
        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(95479, "tv", catId, "Show", 2020, Season: 1, Episode: 8, rv, EpisodeEnd: null));

        ReadItem(itemId).ParsedInfo.Should().Contain("\"episodeEnd\":null").And.NotContain("\"episodeEnd\":9");
    }

    // ---------- Confirm / BindTmdb：失效同目录 series 复用缓存 ----------

    [Fact(DisplayName = "确认后失效同目录 series 复用缓存（键 = 文件父目录，与 ProcessFileService 同构）")]
    public async Task Confirm_Invalidates_FolderSeriesCache_For_Source_Directory()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        MediaItem item = ReadItem(itemId);
        string folderKey = Path.GetDirectoryName(item.SourcePath)!;
        _folderCache.Set(folderKey, new FolderSeriesEntry(111, "tv", "错误匹配的旧剧", 2019, 0.9));
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010).mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, item.RowVersion));

        _folderCache.TryGet(folderKey).Should().BeNull(
            "用户确认（可能更正了 TMDB 绑定）后必须失效 L1 缓存，否则同目录后续文件继续复用旧错误 tmdbId");
    }

    [Fact(DisplayName = "改绑 TMDB 后失效同目录 series 复用缓存")]
    public async Task BindTmdb_Invalidates_FolderSeriesCache_For_Source_Directory()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        MediaItem item = ReadItem(itemId);
        string folderKey = Path.GetDirectoryName(item.SourcePath)!;
        _folderCache.Set(folderKey, new FolderSeriesEntry(111, "tv", "错误匹配的旧剧", 2019, 0.9));
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "盗梦空间", "Inception", 2010, null, null, ["US"], "en", null, null, "{}"));

        await _sut.BindTmdbAsync(itemId, new BindTmdbRequest(27205, "movie", item.RowVersion));

        _folderCache.TryGet(folderKey).Should().BeNull("改绑即用户纠错，L1 旧条目必须立即失效");
    }

    // ---------- Confirm / BindTmdb：TV 绑定沉淀回同目录 series 复用缓存 ----------

    [Fact(DisplayName = "确认 TV 归档后把人工绑定沉淀进同目录缓存（同目录后续文件免搜索免 AI）")]
    public async Task Confirm_Tv_Seeds_FolderSeriesCache_With_Manual_Binding()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        MediaItem item = ReadItem(itemId);
        string folderKey = Path.GetDirectoryName(item.SourcePath)!;
        _folderCache.Set(folderKey, new FolderSeriesEntry(111, "tv", "错误匹配的旧剧", 2019, 0.9));
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "正确的剧", null, 2020, 2, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Tv/Show/S01E01.mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(95479, "tv", catId, "正确的剧", 2020, Season: 1, Episode: 1, item.RowVersion));

        FolderSeriesEntry? seeded = _folderCache.TryGet(folderKey);
        seeded.Should().NotBeNull("人工确认即最终裁决，应沉淀回缓存让同目录后续文件（如周更新集）直接复用");
        seeded!.TmdbId.Should().Be(95479);
        seeded.MediaType.Should().Be("tv");
        seeded.Title.Should().Be("正确的剧");
        seeded.Confidence.Should().Be(1.0);
    }

    [Fact(DisplayName = "改绑 TV 后沉淀新绑定进同目录缓存（旧条目被覆盖）")]
    public async Task BindTmdb_Tv_Seeds_FolderSeriesCache_With_New_Binding()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        MediaItem item = ReadItem(itemId);
        string folderKey = Path.GetDirectoryName(item.SourcePath)!;
        _folderCache.Set(folderKey, new FolderSeriesEntry(111, "tv", "错误匹配的旧剧", 2019, 0.9));
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "改绑后的剧", null, 2021, 1, null, ["US"], "en", null, null, "{}"));

        await _sut.BindTmdbAsync(itemId, new BindTmdbRequest(95479, "tv", item.RowVersion));

        FolderSeriesEntry? seeded = _folderCache.TryGet(folderKey);
        seeded.Should().NotBeNull("改绑 TV 即用户给出正确答案，应沉淀供同目录后续文件复用");
        seeded!.TmdbId.Should().Be(95479);
        seeded.Confidence.Should().Be(1.0);
    }

    // ---------- Confirm：Webhook 旁路（media.skipped / media.failed） ----------

    [Fact(DisplayName = "确认归档同名冲突跳过：发 media.skipped")]
    public async Task Confirm_ConflictSkipped_Emits_MediaSkipped_Webhook()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/CONFLICT.mkv", ArchiveOutcome.ConflictSkipped));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        ReadItem(itemId).Status.Should().Be(MediaItemStatus.Skipped);
        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaSkipped, Arg.Any<object>(), Arg.Any<CancellationToken>());
        ReadSteps(itemId).Should().ContainSingle(s => s.Stage == MediaItemStatus.Skipped, "冲突跳过终态也落时间线");
    }

    [Fact(DisplayName = "确认归档异常判 Failed：发 media.failed（与自动管线旁路对齐）")]
    public async Task Confirm_ArchiveException_Emits_MediaFailed_Webhook()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("disk full"));

        Func<Task> act = async () => await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        await act.Should().ThrowAsync<BusinessException>();
        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "确认归档成功：不发任何 media 事件（media.archived 由 ArchiveService 统一发，不重复）")]
    public async Task Confirm_Completed_Does_Not_Emit_Media_Webhooks()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010).mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        await _webhook.DidNotReceive().EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ---------- Confirm：同名冲突「询问」策略人工裁定覆盖 ----------

    [Fact(DisplayName = "确认 NameCollision 项 = 人工裁定覆盖：走 ForceOverwrite 重载（无条件覆盖），不走普通 2 参重载")]
    public async Task Confirm_NameCollision_Item_Uses_ForceOverwrite_Overload()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai,
            tmdbId: 27205, mediaType: "movie", reviewReason: ReviewReason.NameCollision);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        // 仅显式冲突处理重载（ForceOverwrite）被桩住；若 ConfirmAsync 误走 2 参重载会返回 null → NRE 失败
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), ArchiveOperation.Move,
                ArchiveConflictResolution.ForceOverwrite, Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/Inception (2010).mkv", ArchiveOutcome.Completed));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        ReadItem(itemId).Status.Should().Be(MediaItemStatus.Completed, "人工裁定覆盖后归档完成");
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), ArchiveOperation.Move,
            ArchiveConflictResolution.ForceOverwrite, Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "确认非冲突项 + 询问策略仍冲突：返回 ConflictPending → 退回待确认（标 NameCollision）并发 review.created")]
    public async Task Confirm_NonCollision_StillConflicts_Requeues_To_AwaitingReview()
    {
        long catId = SeedCategory();
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai,
            tmdbId: 27205, mediaType: "movie", reviewReason: ReviewReason.TmdbMultiCandidate);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "Inception", null, 2010, null, null, ["US"], "en", null, null, "{}"));
        // 非 NameCollision 项走普通 2 参重载（FollowPolicy）；询问策略下目标已存在 → ConflictPending
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/M/CONFLICT.mkv", ArchiveOutcome.ConflictPending));

        await _sut.ConfirmAsync(itemId,
            new ConfirmRequest(27205, "movie", catId, "Inception", 2010, null, null, rv));

        MediaItem after = ReadItem(itemId);
        after.Status.Should().Be(MediaItemStatus.AwaitingReview, "确认时仍冲突 → 退回待确认等人工裁定覆盖");
        after.ReviewReason.Should().Be(ReviewReason.NameCollision, "退回原因改记名称冲突");
        after.TargetPath.Should().BeNull("冲突目标是他人产物，绝不写本记录 TargetPath");
        await _webhook.Received(1).EmitAsync(WebhookEvents.ReviewCreated, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ---------- Ignore ----------

    [Fact]
    public async Task Ignore_Transitions_To_Ignored_With_Reason()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        IgnoreResult r = await _sut.IgnoreAsync(itemId, new IgnoreRequest(rv, "测试样本"));

        r.Status.Should().Be(MediaItemStatus.Ignored);
        MediaItem final = ReadItem(itemId);
        final.Status.Should().Be(MediaItemStatus.Ignored);
        final.ErrorMessage.Should().Contain("测试样本");
    }

    [Fact]
    public async Task Ignore_RowVersion_Mismatch_Throws()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        Func<Task> act = async () => await _sut.IgnoreAsync(itemId, new IgnoreRequest(RowVersion: 9999, Reason: null));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*已被其他用户修改*");
    }

    [Fact(DisplayName = "人工忽略落时间线：Ignored 步骤含中文原因")]
    public async Task Ignore_Appends_Ignored_Timeline_Step_With_Reason()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        await _sut.IgnoreAsync(itemId, new IgnoreRequest(rv, "测试样本"));

        ProcessStep step = ReadSteps(itemId).Should().ContainSingle(s => s.Stage == MediaItemStatus.Ignored,
            "人工状态改写必须可在详情时间线追溯").Which;
        step.Detail.Should().Contain("用户在审核页忽略").And.Contain("测试样本");
    }

    [Fact(DisplayName = "人工忽略未填原因：时间线仍落步骤（通用文案）")]
    public async Task Ignore_Without_Reason_Still_Appends_Timeline_Step()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        await _sut.IgnoreAsync(itemId, new IgnoreRequest(rv, null));

        ReadSteps(itemId).Single(s => s.Stage == MediaItemStatus.Ignored)
            .Detail.Should().Contain("用户在审核页忽略");
    }

    // ---------- Batch Confirm ----------

    [Fact]
    public async Task BatchConfirm_All_Success_Returns_Succeeded()
    {
        long catId = SeedCategory();
        long a = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long b = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule);
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(1, "movie", "T", null, 2020, null, null, null, null, null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/p", ArchiveOutcome.Completed));

        BatchConfirmResult r = await _sut.BatchConfirmAsync(new BatchConfirmRequest(new[]
        {
            new BatchConfirmItem(a, 1, "movie", catId, null, 2020, null, null, ReadItem(a).RowVersion),
            new BatchConfirmItem(b, 1, "movie", catId, null, 2020, null, null, ReadItem(b).RowVersion),
        }));

        r.Succeeded.Should().BeEquivalentTo(new[] { a, b });
        r.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchConfirm_Partial_Failure_Returned_In_Failed_Array()
    {
        long catId = SeedCategory();
        long a = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long b = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule);
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(1, "movie", "T", null, 2020, null, null, null, null, null, null, "{}"));
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/p", ArchiveOutcome.Completed));

        BatchConfirmResult r = await _sut.BatchConfirmAsync(new BatchConfirmRequest(new[]
        {
            new BatchConfirmItem(a, 1, "movie", catId, null, 2020, null, null, ReadItem(a).RowVersion),
            new BatchConfirmItem(b, 1, "movie", catId, null, 2020, null, null, RowVersion: 9999), // 错版本
        }));

        r.Succeeded.Should().Equal(a);
        r.Failed.Should().ContainSingle().Which.Id.Should().Be(b);
        r.Failed[0].Message.Should().Contain("已被其他用户修改");
    }

    // BatchConfirm 的 items 空 / 超上限校验已迁为 DTO 反射单测（Application.Tests/Validation/ReviewRequestValidationTests）

    // ---------- TMDB Search ----------
    // 说明：原 TmdbSearch_Empty_Query_Throws（"搜索词不能为空"）已迁为 DTO 反射单测
    // （PersonalMediaManager.Application.Tests/Validation/ReviewRequestValidationTests），
    // 该校验改由 TmdbSearchListQuery DataAnnotations 在模型绑定阶段承接，service 不再抛异常。

    [Fact]
    public async Task TmdbSearch_NonExisting_MediaItem_Throws()
    {
        Func<Task> act = async () => await _sut.TmdbSearchAsync(99999, new TmdbSearchListQuery("foo"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("记录不存在");
    }

    [Fact]
    public async Task TmdbSearch_Returns_Candidates_With_Poster_Urls()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult(new[]
            {
                new TmdbCandidate(27205, "movie", "盗梦空间", "Inception", 2010, 84.5, "en", new[] { "US" }, "/p.jpg", null),
            }, null));

        TmdbSearchListResult r = await _sut.TmdbSearchAsync(itemId, new TmdbSearchListQuery("Inception"));

        r.Items.Should().ContainSingle();
        r.Items[0].TmdbId.Should().Be(27205);
        r.Items[0].PosterUrl.Should().Contain("/p.jpg");
    }

    [Fact]
    public async Task TmdbSearch_TmdbException_Maps_To_9000_BusinessError()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Throws(new TmdbClientException("500", 500));
        Func<Task> act = async () => await _sut.TmdbSearchAsync(itemId, new TmdbSearchListQuery("x"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("TMDB 服务异常");
    }

    // ---------- Bind TMDB ----------

    [Fact]
    public async Task BindTmdb_Updates_TmdbId_And_Title_Without_ChangingStatus()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(27205, "movie", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(27205, "movie", "盗梦空间", "Inception", 2010, null, null, ["US"], "en", null, null, "{}"));

        BindTmdbResult r = await _sut.BindTmdbAsync(itemId, new BindTmdbRequest(27205, "movie", rv));

        r.TmdbId.Should().Be(27205);
        r.Title.Should().Be("盗梦空间");
        r.Year.Should().Be(2010);
        MediaItem after = ReadItem(itemId);
        after.Status.Should().Be(MediaItemStatus.AwaitingReview, "bind-tmdb 不改 status");
        after.TmdbId.Should().Be(27205);
    }

    [Fact]
    public async Task BindTmdb_InvalidMediaType_Throws()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        Func<Task> act = async () => await _sut.BindTmdbAsync(itemId, new BindTmdbRequest(1, "weird", rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*mediaType*非法*");
    }

    [Fact]
    public async Task BindTmdb_TmdbException_Maps_To_Invalid_TmdbId()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new TmdbClientException("404", 404));
        Func<Task> act = async () => await _sut.BindTmdbAsync(itemId, new BindTmdbRequest(99999999, "movie", rv));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*TMDB ID 无效*");
    }

    [Fact]
    public async Task BindTmdb_RowVersion_Mismatch_Throws()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        Func<Task> act = async () => await _sut.BindTmdbAsync(itemId, new BindTmdbRequest(1, "movie", RowVersion: 9999));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*已被其他用户修改*");
    }

    // ---------- TMDB Detail (按 ID 取详情) ----------

    [Fact]
    public async Task TmdbDetail_Returns_Details_With_Poster_And_TotalSeasons()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        _tmdb.GetDetailsAsync(95479, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(95479, "tv", "咒术回战", "呪術廻戦", 2020, 2, "/p.jpg", ["JP"], "ja", null, null, "{}"));

        TmdbDetailItem r = await _sut.TmdbDetailAsync(itemId, new TmdbDetailQuery(95479, "tv"));

        r.TmdbId.Should().Be(95479);
        r.Title.Should().Be("咒术回战");
        r.Year.Should().Be(2020);
        r.TotalSeasons.Should().Be(2);
        r.PosterUrl.Should().Contain("/p.jpg");
        r.OriginCountry.Should().Contain("JP");
    }

    [Fact]
    public async Task TmdbDetail_Returns_Per_Season_Episode_Counts()
    {
        // 绝对集号换算依赖逐季集数透传：mock 返回 Seasons，断言 DTO 原样映射（含特别篇季 0）
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        _tmdb.GetDetailsAsync(120089, "tv", Arg.Any<CancellationToken>())
            .Returns(new TmdbDetailsResult(120089, "tv", "间谍过家家", "SPY×FAMILY", 2022, 2, "/p.jpg", ["JP"], "ja", null, null, "{}",
                Seasons: [new TmdbSeasonInfo(0, 5), new TmdbSeasonInfo(1, 25), new TmdbSeasonInfo(2, 12)]));

        TmdbDetailItem r = await _sut.TmdbDetailAsync(itemId, new TmdbDetailQuery(120089, "tv"));

        r.Seasons.Should().NotBeNull();
        r.Seasons!.Select(s => (s.SeasonNumber, s.EpisodeCount))
            .Should().Equal((0, 5), (1, 25), (2, 12));
    }

    [Fact]
    public async Task TmdbDetail_NonExisting_MediaItem_Throws()
    {
        Func<Task> act = async () => await _sut.TmdbDetailAsync(99999, new TmdbDetailQuery(1, "movie"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("记录不存在");
    }

    [Fact]
    public async Task TmdbDetail_InvalidMediaType_Throws()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        Func<Task> act = async () => await _sut.TmdbDetailAsync(itemId, new TmdbDetailQuery(1, "weird"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*mediaType*非法*");
    }

    [Fact]
    public async Task TmdbDetail_TmdbException_Maps_To_Invalid_TmdbId()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        _tmdb.GetDetailsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new TmdbClientException("404", 404));
        Func<Task> act = async () => await _sut.TmdbDetailAsync(itemId, new TmdbDetailQuery(99999999, "movie"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*TMDB ID 无效*");
    }

    // ---------- Preview Paths (去向预览) ----------

    [Fact]
    public async Task PreviewPaths_Movie_Computes_Plex_Path_With_TmdbTag()
    {
        long catId = SeedCategory(); // TargetRoot=/M
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k1", 27205, "movie", "Inception", 2010, null, null, null, catId, "Inception.2010.mkv"),
        }));

        ReviewPreviewPathEntry e = r.Entries.Single();
        e.Key.Should().Be("k1");
        e.Error.Should().BeNull();
        e.RelativePath.Should().Contain("Inception (2010)").And.Contain("{tmdb-27205}").And.EndWith(".mkv");
        e.FullPath.Should().Contain("Inception (2010)");
    }

    [Fact]
    public async Task PreviewPaths_Tv_Computes_Season_And_Episode_Path()
    {
        long catId = SeedCategory();
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 95479, "tv", "JJK", 2020, 2, 5, null, catId, "JJK.S02E05.mkv"),
        }));

        ReviewPreviewPathEntry e = r.Entries.Single();
        e.Error.Should().BeNull();
        e.RelativePath.Should().Contain("JJK (2020)").And.Contain("Season 02").And.Contain("S02E05");
    }

    [Fact]
    public async Task PreviewPaths_Tv_EpisodeEnd_Produces_Range()
    {
        long catId = SeedCategory();
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 1, "tv", "Show", 2020, 1, 8, 9, catId, "x.mkv"),
        }));

        r.Entries.Single().RelativePath.Should().Contain("S01E08-E09");
    }

    [Fact]
    public async Task PreviewPaths_Missing_Year_Returns_Error_Not_Path()
    {
        long catId = SeedCategory();
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 1, "movie", "NoYear", null, null, null, null, catId, "x.mkv"),
        }));

        ReviewPreviewPathEntry e = r.Entries.Single();
        e.RelativePath.Should().BeNull();
        e.Error.Should().Contain("年份");
    }

    [Fact]
    public async Task PreviewPaths_Tv_Missing_Season_Returns_Error()
    {
        long catId = SeedCategory();
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 1, "tv", "Show", 2020, null, 5, null, catId, "x.mkv"),
        }));

        r.Entries.Single().Error.Should().Contain("季");
    }

    [Fact]
    public async Task PreviewPaths_Unknown_Category_Returns_Error_Per_Item()
    {
        // 单条分类不存在不应让整批抛异常，仅该条带 Error
        long catId = SeedCategory();
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("ok", 27205, "movie", "Inception", 2010, null, null, null, catId, "a.mkv"),
            new ReviewPreviewPathItem("bad", 1, "movie", "T", 2020, null, null, null, CategoryId: 9999, "b.mkv"),
        }));

        r.Entries.Should().HaveCount(2);
        r.Entries.Single(x => x.Key == "ok").Error.Should().BeNull();
        r.Entries.Single(x => x.Key == "bad").Error.Should().Contain("分类不存在");
    }

    [Fact]
    public async Task PreviewPaths_No_Extension_Falls_Back_To_Mkv()
    {
        long catId = SeedCategory();
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 27205, "movie", "Inception", 2010, null, null, null, catId, "Inception_no_ext"),
        }));

        r.Entries.Single().RelativePath.Should().EndWith(".mkv");
    }

    [Fact(DisplayName = "去向预览：标题段用 TMDB 规范名（缓存命中时覆盖入参标题，与实际归档一致）")]
    public async Task PreviewPaths_Uses_Canonical_TmdbTitle_When_Cached()
    {
        long catId = SeedCategory();
        SeedTmdbCache(120089, "movie", "间谍过家家", "SPY×FAMILY", 2022, "/p.jpg");
        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 120089, "movie", "Spy x Family", 2022, null, null, null, catId, "Spy.x.Family.2022.mkv"),
        }));

        ReviewPreviewPathEntry e = r.Entries.Single();
        e.Error.Should().BeNull();
        e.RelativePath.Should().Contain("间谍过家家 (2022)").And.Contain("{tmdb-120089}");
        e.RelativePath.Should().NotContain("Spy x Family", "标题段应用 TMDB 规范名，不跟随入参解析 / 候选名");
    }

    [Fact(DisplayName = "去向预览：分类根已存在同 tmdbId 目录（异名）→ FullPath 落入复用目录")]
    public async Task PreviewPaths_Reuses_Existing_TmdbFolder()
    {
        string legacyFolder = Path.Combine(_scratch, "SPY×FAMILY (2022) {tmdb-120089}");
        Directory.CreateDirectory(legacyFolder);
        long catId = SeedCategory(_scratch);
        SeedTmdbCache(120089, "movie", "间谍过家家", "SPY×FAMILY", 2022, "/p.jpg");

        ReviewPreviewPathResult r = await _sut.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
        {
            new ReviewPreviewPathItem("k", 120089, "movie", "间谍过家家", 2022, null, null, null, catId, "x.mkv"),
        }));

        ReviewPreviewPathEntry e = r.Entries.Single();
        e.Error.Should().BeNull();
        e.FullPath.Should().StartWith(legacyFolder + Path.DirectorySeparatorChar,
            "预览 FullPath 应落入已存在的同 tmdbId 目录（与实际归档一致）");
    }

    // PreviewPaths 的 items 空校验已迁为 DTO 反射单测（见 ReviewRequestValidationTests）

    // ---------- Check Files (文件存在性检查) ----------

    [Fact]
    public async Task CheckFiles_Missing_Source_Transitions_To_Ignored_And_Returned_In_Removed()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        // _fileProbe 默认对任意路径返回 false → 视为源文件已不存在

        CheckFilesResult r = await _sut.CheckFilesAsync(new CheckFilesRequest(new[] { new CheckFileItem(itemId, rv) }));

        r.Removed.Should().Equal(itemId);
        r.Kept.Should().Be(0);
        r.Failed.Should().BeEmpty();

        MediaItem final = ReadItem(itemId);
        final.Status.Should().Be(MediaItemStatus.Ignored, "源文件不存在 → 转 Ignored 移出队列");
        final.ErrorMessage.Should().Contain("源文件已不存在");
    }

    [Fact(DisplayName = "文件检查移除落时间线：Ignored 步骤含「源文件已不存在」原因")]
    public async Task CheckFiles_Removed_Item_Appends_Ignored_Timeline_Step()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        // _fileProbe 默认对任意路径返回 false → 视为源文件已不存在

        await _sut.CheckFilesAsync(new CheckFilesRequest(new[] { new CheckFileItem(itemId, rv) }));

        ProcessStep step = ReadSteps(itemId).Should().ContainSingle(s => s.Stage == MediaItemStatus.Ignored).Which;
        step.Detail.Should().Contain("源文件已不存在");
    }

    [Fact]
    public async Task CheckFiles_Existing_Source_Kept_In_Queue()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;
        _fileProbe.FileExists(ReadItem(itemId).SourcePath).Returns(true);

        CheckFilesResult r = await _sut.CheckFilesAsync(new CheckFilesRequest(new[] { new CheckFileItem(itemId, rv) }));

        r.Removed.Should().BeEmpty();
        r.Kept.Should().Be(1);
        ReadItem(itemId).Status.Should().Be(MediaItemStatus.AwaitingReview, "文件仍在 → 留在队列");
    }

    [Fact]
    public async Task CheckFiles_Mixed_Partitions_Removed_And_Kept()
    {
        long gone = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        long alive = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Rule);
        _fileProbe.FileExists(ReadItem(alive).SourcePath).Returns(true); // gone 路径未配置 → 默认 false

        CheckFilesResult r = await _sut.CheckFilesAsync(new CheckFilesRequest(new[]
        {
            new CheckFileItem(gone, ReadItem(gone).RowVersion),
            new CheckFileItem(alive, ReadItem(alive).RowVersion),
        }));

        r.Removed.Should().Equal(gone);
        r.Kept.Should().Be(1);
        r.Failed.Should().BeEmpty();
        ReadItem(gone).Status.Should().Be(MediaItemStatus.Ignored);
        ReadItem(alive).Status.Should().Be(MediaItemStatus.AwaitingReview);
    }

    [Fact]
    public async Task CheckFiles_RowVersion_Mismatch_Goes_To_Failed_Without_Change()
    {
        long itemId = SeedItem(MediaItemStatus.AwaitingReview, ParseSource.Ai);
        // 文件不存在但版本不匹配：不应被移除，进 Failed，记录保持原状

        CheckFilesResult r = await _sut.CheckFilesAsync(new CheckFilesRequest(new[] { new CheckFileItem(itemId, 9999) }));

        r.Removed.Should().BeEmpty();
        r.Failed.Should().ContainSingle().Which.Message.Should().Contain("已被其他用户修改");
        ReadItem(itemId).Status.Should().Be(MediaItemStatus.AwaitingReview);
    }

    [Fact]
    public async Task CheckFiles_NonExisting_Id_Goes_To_Failed()
    {
        CheckFilesResult r = await _sut.CheckFilesAsync(new CheckFilesRequest(new[] { new CheckFileItem(99999, 0) }));

        r.Removed.Should().BeEmpty();
        r.Failed.Should().ContainSingle().Which.Message.Should().Be("记录不存在");
    }

    [Fact]
    public async Task CheckFiles_NotInAwaitingReview_Goes_To_Failed()
    {
        long itemId = SeedItem(MediaItemStatus.Completed, ParseSource.Ai);
        long rv = ReadItem(itemId).RowVersion;

        CheckFilesResult r = await _sut.CheckFilesAsync(new CheckFilesRequest(new[] { new CheckFileItem(itemId, rv) }));

        r.Removed.Should().BeEmpty();
        r.Failed.Should().ContainSingle().Which.Message.Should().Contain("待确认状态");
    }

    // CheckFiles 的 items 空 / 超上限校验已迁为 DTO 反射单测（见 ReviewRequestValidationTests）

    // ---------- ParsedInfo 值对象（替代旧 ReviewService.MergeOrCreateParsedInfo） ----------

    [Fact]
    public void ParsedInfo_MergeWith_Override_Wins_Over_Existing()
    {
        const string existing = """{"title":"OldName","year":2000,"type":"movie","season":null,"episode":null,"matchedRuleId":null}""";
        string merged = ParsedInfo.FromJson(existing)!.MergeWith("NewName", 2020, "movie", null, null).ToJson();
        merged.Should().Contain("NewName").And.Contain("2020");
    }

    [Fact]
    public void ParsedInfo_MergeWith_Null_Override_Keeps_Existing_Value()
    {
        const string existing = """{"title":"Keep","year":1999,"type":"movie","season":1,"episode":2,"matchedRuleId":null}""";
        string merged = ParsedInfo.FromJson(existing)!.MergeWith(null, null, "tv", null, null).ToJson();
        merged.Should().Contain("Keep").And.Contain("1999").And.Contain("\"season\":1").And.Contain("\"episode\":2");
    }

    // ---------- helpers ----------

    private MediaItem ReadItem(long id)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.MediaItems.AsNoTracking().Single(m => m.Id == id);
    }

    private List<ProcessStep> ReadSteps(long id)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.ProcessSteps.AsNoTracking()
            .Where(s => s.MediaItemId == id)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.Id)
            .ToList();
    }

    private long SeedCategory() => SeedCategory("/M");

    private long SeedCategory(string targetRoot)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new() { Name = $"Cat-{Guid.NewGuid():N}", MediaType = MediaType.Both, TargetRoot = targetRoot };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    private long SeedItem(MediaItemStatus status, ParseSource source, int? tmdbId = null, string? mediaType = null, string? parsedInfo = null, ReviewReason? reviewReason = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string path = $"/tmp/{Guid.NewGuid():N}.mkv";
        MediaItem item = MediaItem.CreateFixture(
            path, Path.GetFileName(path), 1024,
            status: status,
            parseSource: source,
            confidence: 0.5,
            parsedInfo: parsedInfo ?? """{"title":"OriginalTitle","year":2020,"type":"movie"}""",
            tmdbId: tmdbId,
            tmdbMediaType: mediaType,
            reviewReason: reviewReason);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private long SeedItemWithCandidates(string candidatesJson, int? tmdbId = null, string? mediaType = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string path = $"/tmp/{Guid.NewGuid():N}.mkv";
        MediaItem item = MediaItem.CreateFixture(
            path, Path.GetFileName(path), 1024,
            status: MediaItemStatus.AwaitingReview,
            parseSource: ParseSource.Ai,
            parsedInfo: """{"title":"X","year":2020,"type":"movie"}""",
            tmdbId: tmdbId,
            tmdbMediaType: mediaType,
            tmdbCandidatesJson: candidatesJson);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private void SeedTmdbCache(int tmdbId, string mediaType, string title, string originalTitle, int year, string posterPath, int? totalSeasons = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.TmdbMetadataCaches.Add(new TmdbMetadataCache
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            OriginalTitle = originalTitle,
            Year = year,
            PosterPath = posterPath,
            TotalSeasons = totalSeasons,
            CachedAt = DateTimeOffset.UtcNow,
            RawJson = "{}",
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

    /// <summary>测试用 IFolderSeriesCache：带真实 Remove 语义（钉住确认 / 改绑触发的缓存失效与键一致性）</summary>
    private sealed class TestFolderSeriesCache : IFolderSeriesCache
    {
        private readonly Dictionary<string, FolderSeriesEntry> _map = new(StringComparer.OrdinalIgnoreCase);
        public FolderSeriesEntry? TryGet(string folderPath) =>
            _map.TryGetValue(folderPath, out FolderSeriesEntry? e) ? e : null;
        public void Set(string folderPath, FolderSeriesEntry entry) => _map[folderPath] = entry;
        public void Remove(string folderPath) => _map.Remove(folderPath);
    }
}
