using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.History;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.History;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D7.8 HistoryService — list 过滤分页 + rescan 状态机 + 源文件存在性</summary>
public sealed class HistoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly InMemoryQueue _queue = new();
    private readonly TaskCancellationManager _taskCancellation = new();
    private readonly IArchiveService _archive = Substitute.For<IArchiveService>();
    private readonly IFileMover _fileMover = Substitute.For<IFileMover>();
    private readonly IFileProbe _fileProbe = Substitute.For<IFileProbe>();
    private readonly IWebhookEmitter _webhook = Substitute.For<IWebhookEmitter>();
    private readonly Application.Services.Parse.IFolderSeriesCache _folderCache =
        Substitute.For<Application.Services.Parse.IFolderSeriesCache>();
    private readonly HistoryService _sut;
    private readonly List<string> _tempFiles = new();

    public HistoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new HistoryService(_dbFactory, _queue, _taskCancellation, _archive, _fileMover, _fileProbe, _webhook, _folderCache,
            new NullTaskNotifier(),
            AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-hist-{Guid.NewGuid():N}")), NullLogger<HistoryService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (string f in _tempFiles) { try { File.Delete(f); } catch { } }
    }

    // ---------- List ----------

    [Fact]
    public async Task Cancel_Queued_MarksCancelled_And_AppendsTimeline()
    {
        string sourcePath = WriteTempFile();
        long id = SeedItem(MediaItemStatus.Queued, sourcePath: sourcePath);

        CancelTaskResult result = await _sut.CancelAsync(id);

        result.Status.Should().Be(MediaItemStatus.Cancelled);
        ReadItem(id).Status.Should().Be(MediaItemStatus.Cancelled);
        (await _sut.GetDetailAsync(id)).Steps.Should().ContainSingle(s =>
            s.Stage == MediaItemStatus.Cancelled && s.Detail!.Contains("用户主动取消"));
        using TaskCancellationRegistration registration = _taskCancellation.Register(sourcePath, CancellationToken.None);
        registration.IsCancellationRequested.Should().BeTrue("排队项出队时必须立即跳过写入完成检测和后续管线");
    }

    [Fact]
    public async Task Cancel_Running_CancelsActiveToken_BeforeMarkingCancelled()
    {
        string sourcePath = WriteTempFile();
        long id = SeedItem(MediaItemStatus.Parsing, sourcePath: sourcePath);
        using TaskCancellationRegistration registration = _taskCancellation.Register(sourcePath, CancellationToken.None);

        Task<CancelTaskResult> cancelling = _sut.CancelAsync(id);
        await WaitUntilAsync(() => registration.IsCancellationRequested);
        cancelling.IsCompleted.Should().BeFalse("活动处理尚未退出前不能提前落取消终态");

        registration.Dispose();
        CancelTaskResult result = await cancelling;

        result.Status.Should().Be(MediaItemStatus.Cancelled);
        ReadItem(id).Status.Should().Be(MediaItemStatus.Cancelled);
    }

    [Theory]
    [InlineData(MediaItemStatus.Completed)]
    [InlineData(MediaItemStatus.Archiving)]
    public async Task Cancel_NonCancellableState_Rejects_WithoutChangingStatus(MediaItemStatus status)
    {
        long id = SeedItem(status);

        Func<Task> act = () => _sut.CancelAsync(id);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*正在处理*");
        ReadItem(id).Status.Should().Be(status);
    }

    [Fact]
    public async Task List_Returns_All_With_Default_Query()
    {
        for (int i = 0; i < 3; i++) SeedItem(MediaItemStatus.Completed);
        SeedItem(MediaItemStatus.Failed);

        HistoryListPage page = await _sut.ListAsync(new HistoryListQuery());

        page.Total.Should().Be(4);
        page.Items.Should().HaveCount(4);
    }

    [Fact]
    public async Task List_Filter_By_Status()
    {
        SeedItem(MediaItemStatus.Completed);
        SeedItem(MediaItemStatus.Completed);
        SeedItem(MediaItemStatus.Failed);

        HistoryListPage p = await _sut.ListAsync(new HistoryListQuery(Status: MediaItemStatus.Completed));
        p.Total.Should().Be(2);
    }

    [Fact]
    public async Task List_Filter_By_ParseSource()
    {
        SeedItem(MediaItemStatus.Completed, source: ParseSource.Rule);
        SeedItem(MediaItemStatus.Completed, source: ParseSource.Ai);

        HistoryListPage p = await _sut.ListAsync(new HistoryListQuery(ParseSource: ParseSource.Rule));
        p.Items.Should().ContainSingle();
        p.Items[0].ParseSource.Should().Be(ParseSource.Rule);
    }

    [Fact]
    public async Task List_Filter_By_CategoryId()
    {
        long catId = SeedCategory("电影");
        SeedItem(MediaItemStatus.Completed, categoryId: catId);
        SeedItem(MediaItemStatus.Completed); // 无分类

        HistoryListPage p = await _sut.ListAsync(new HistoryListQuery(CategoryId: catId));
        p.Items.Should().ContainSingle();
        p.Items[0].Category.Should().Be("电影");
    }

    [Fact]
    public async Task List_Filter_By_From_To_TimeRange()
    {
        long recent = SeedItem(MediaItemStatus.Completed);
        long old = SeedItem(MediaItemStatus.Completed);
        SetUpdatedAt(old, DateTimeOffset.UtcNow.AddDays(-30));

        DateTimeOffset from = DateTimeOffset.UtcNow.AddHours(-1);
        HistoryListPage p = await _sut.ListAsync(new HistoryListQuery(From: from));
        p.Items.Should().ContainSingle(i => i.Id == recent);
    }

    [Fact]
    public async Task List_Filter_By_Keyword_Matches_FileName_Or_ParsedInfoTitle()
    {
        SeedItemWithParsedInfo(MediaItemStatus.Completed, "movie_a.mkv", """{"title":"盗梦空间","year":2010}""");
        SeedItemWithParsedInfo(MediaItemStatus.Completed, "movie_b.mkv", """{"title":"星际穿越","year":2014}""");

        HistoryListPage byFile = await _sut.ListAsync(new HistoryListQuery(Keyword: "movie_a"));
        byFile.Items.Should().ContainSingle();

        HistoryListPage byTitle = await _sut.ListAsync(new HistoryListQuery(Keyword: "盗梦"));
        byTitle.Items.Should().ContainSingle();
    }

    // 说明：原 List_Invalid_Page_Throws（"分页参数非法"，page ≥ 1）已迁为 DTO 反射单测
    // （PersonalMediaManager.Application.Tests/Validation/HistoryRequestValidationTests），
    // 该校验改由 HistoryListQuery / PendingListQuery DataAnnotations 在模型绑定阶段承接。
    // From>To 为跨字段校验，仍留在 service（见 List_From_GreaterThan_To_Throws）。

    [Fact]
    public async Task List_From_GreaterThan_To_Throws()
    {
        Func<Task> act = async () => await _sut.ListAsync(new HistoryListQuery(
            From: DateTimeOffset.UtcNow, To: DateTimeOffset.UtcNow.AddHours(-1)));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("时间区间格式错误");
    }

    [Fact]
    public async Task List_OrderBy_UpdatedAt_Desc()
    {
        long a = SeedItem(MediaItemStatus.Completed);
        long b = SeedItem(MediaItemStatus.Completed);
        SetUpdatedAt(a, DateTimeOffset.UtcNow.AddMinutes(-5));
        // b 默认 UpdatedAt=now，更新

        HistoryListPage p = await _sut.ListAsync(new HistoryListQuery());
        p.Items[0].Id.Should().Be(b, "最新 UpdatedAt 在前");
    }

    // ---------- 待补元数据徽标（MetadataPending）----------
    // 口径：该记录最后一条 stage=Archiving 时间线步骤的 detail 含 "metadataPending":true（见 LoadMetadataPendingMapAsync 注释）

    [Fact(DisplayName = "列表徽标：带警告归档（视频落地但元数据降级）→ MetadataPending=true")]
    public async Task List_MetadataPending_True_After_DegradedArchive()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.Completed,
                new[] { "元数据(nfo)写入失败：网络盘只读" }));

        await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        HistoryListPage page = await _sut.ListAsync(new HistoryListQuery());

        page.Items.Should().ContainSingle(i => i.Id == id)
            .Which.MetadataPending.Should().BeTrue("最近一次归档带降级警告，列表行须点亮「待补元数据」徽标");
    }

    [Fact(DisplayName = "列表徽标：带警告归档 → 退回重改 → 重新成功归档（无警告）→ 徽标熄灭")]
    public async Task List_MetadataPending_False_After_Reopen_Then_CleanRearchive()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.Completed,
                new[] { "元数据(nfo)写入失败：网络盘只读" }));
        await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        (await _sut.ListAsync(new HistoryListQuery())).Items.Single(i => i.Id == id)
            .MetadataPending.Should().BeTrue("前置：带警告归档先点亮徽标");

        // 退回重改：归档文件移回源位（追加一条不带 metadataPending 的 Archiving 移回步骤）→ 徽标即刻熄灭
        _fileProbe.FileExists("/Media/X (2010)/X (2010).mkv").Returns(true);
        await _sut.ReopenForReviewAsync(id);
        (await _sut.ListAsync(new HistoryListQuery())).Items.Single(i => i.Id == id)
            .MetadataPending.Should().BeFalse("退回重改后最后一条 Archiving 步骤为移回动作，徽标必须熄灭");

        // 重新成功归档（无警告）→ 保持熄灭
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.Completed));
        await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        (await _sut.ListAsync(new HistoryListQuery())).Items.Single(i => i.Id == id)
            .MetadataPending.Should().BeFalse("重新确认成功（无警告）后徽标保持熄灭");
    }

    [Fact(DisplayName = "列表徽标：从未带警告（正常归档 / 无任何时间线）→ MetadataPending=false")]
    public async Task List_MetadataPending_False_When_Never_Warned()
    {
        // 正常归档：Archiving 步骤省 null 不落 metadataPending 字段
        long catId = SeedCategoryWithRoot("/Media");
        long cleanArchived = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/clean.mkv");
        _fileProbe.FileExists("/tmp/clean.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.Completed));
        await _sut.ManualArchiveAsync(cleanArchived, new ManualArchiveRequest("Move"));
        // 直接 seed 的终态记录：无任何 Archiving 时间线
        long noSteps = SeedItem(MediaItemStatus.Completed);

        HistoryListPage page = await _sut.ListAsync(new HistoryListQuery());

        page.Items.Single(i => i.Id == cleanArchived).MetadataPending.Should().BeFalse("正常归档不落 metadataPending 字段");
        page.Items.Single(i => i.Id == noSteps).MetadataPending.Should().BeFalse("无 Archiving 时间线的记录徽标熄灭");
    }

    // ---------- Rescan ----------

    [Fact]
    public async Task Rescan_Failed_Transitions_To_Queued_And_Enqueues()
    {
        string src = WriteTempFile();
        long id = SeedItemAtFailedWithSource(src);

        RescanResult r = await _sut.RescanAsync(id);

        r.Status.Should().Be(MediaItemStatus.Queued);
        ReadItem(id).Status.Should().Be(MediaItemStatus.Queued);
        ReadItem(id).ErrorMessage.Should().BeNull("重投清理旧错误信息");
        _queue.Items.Should().ContainSingle().Which.FullPath.Should().Be(src);
        _queue.Items[0].Source.Should().Be(PendingFileSource.Manual);
    }

    [Fact]
    public async Task Rescan_NonExisting_Throws_NotFound()
    {
        Func<Task> act = async () => await _sut.RescanAsync(99999);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("记录不存在");
    }

    [Fact]
    public async Task Rescan_NonFailed_Status_Throws_InProgress()
    {
        string src = WriteTempFile();
        long id = SeedItem(MediaItemStatus.Completed, sourcePath: src);
        Func<Task> act = async () => await _sut.RescanAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*正在处理中*");
    }

    [Fact]
    public async Task Rescan_Resolves_WatchFolderId_By_SourcePath_Prefix()
    {
        // 临时目录作监控根；MediaItem.SourcePath 落在该目录下任一子文件
        string watchRoot = Path.Combine(Path.GetTempPath(), $"pmm-w-{Guid.NewGuid():N}");
        Directory.CreateDirectory(watchRoot);
        try
        {
            string src = Path.Combine(watchRoot, "国务卿女士 6季", "第1季", "01.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            File.WriteAllText(src, "x");
            _tempFiles.Add(src);

            long folderId = SeedWatchFolder(watchRoot);
            long id = SeedItemAtFailedWithSource(src);

            await _sut.RescanAsync(id);

            _queue.Items.Should().ContainSingle();
            _queue.Items[0].WatchFolderId.Should().Be(folderId,
                "Rescan 应反查路径前缀命中的 WatchFolder，保证 RelativeSegments 不丢失");
        }
        finally
        {
            try { Directory.Delete(watchRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Rescan_Falls_Back_To_Zero_When_No_WatchFolder_Matches()
    {
        // 没 seed 任何 WatchFolder → 反查必然找不到，回退 0（保持旧行为）
        string src = WriteTempFile();
        long id = SeedItemAtFailedWithSource(src);

        await _sut.RescanAsync(id);

        _queue.Items.Should().ContainSingle();
        _queue.Items[0].WatchFolderId.Should().Be(0);
    }

    [Fact]
    public async Task Rescan_Picks_Longest_Prefix_When_WatchFolders_Nested()
    {
        // 嵌套监控目录：outer = /tmp/pmm-x；inner = /tmp/pmm-x/sub；SourcePath 落在 inner 下
        // 期望命中 inner（最长前缀），与 FileWatcher 事件归属语义一致
        string outer = Path.Combine(Path.GetTempPath(), $"pmm-x-{Guid.NewGuid():N}");
        string inner = Path.Combine(outer, "sub");
        Directory.CreateDirectory(inner);
        try
        {
            string src = Path.Combine(inner, "movie.mkv");
            File.WriteAllText(src, "x");
            _tempFiles.Add(src);

            long outerId = SeedWatchFolder(outer);
            long innerId = SeedWatchFolder(inner);
            long id = SeedItemAtFailedWithSource(src);

            await _sut.RescanAsync(id);

            _queue.Items[0].WatchFolderId.Should().Be(innerId, "嵌套监控目录取最长前缀匹配");
            _queue.Items[0].WatchFolderId.Should().NotBe(outerId);
        }
        finally
        {
            try { Directory.Delete(outer, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Rescan_Missing_SourceFile_Throws()
    {
        // SeedItem 用一个未创建的临时文件名
        string ghost = Path.Combine(Path.GetTempPath(), $"pmm-h-{Guid.NewGuid():N}.mkv");
        long id = SeedItemAtFailedWithSource(ghost);

        Func<Task> act = async () => await _sut.RescanAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("源文件已不存在*");
    }

    // ---------- 批量重试 ----------

    [Fact(DisplayName = "批量重试：所有 Failed 重投 Queued，源文件缺失的跳过计数，非 Failed 不动")]
    public async Task RescanAllFailed_Requeues_Existing_Skips_Missing()
    {
        string src1 = WriteTempFile();
        string src2 = WriteTempFile();
        long id1 = SeedItemAtFailedWithSource(src1);
        long id2 = SeedItemAtFailedWithSource(src2);
        string ghost = Path.Combine(Path.GetTempPath(), $"pmm-h-{Guid.NewGuid():N}.mkv");
        long idGhost = SeedItemAtFailedWithSource(ghost);
        long completed = SeedItem(MediaItemStatus.Completed);

        BatchRescanResult r = await _sut.RescanAllFailedAsync();

        r.Requeued.Should().Be(2);
        r.Skipped.Should().Be(1);
        ReadItem(id1).Status.Should().Be(MediaItemStatus.Queued);
        ReadItem(id2).Status.Should().Be(MediaItemStatus.Queued);
        ReadItem(idGhost).Status.Should().Be(MediaItemStatus.Failed, "源文件缺失的保持 Failed");
        ReadItem(completed).Status.Should().Be(MediaItemStatus.Completed, "非 Failed 不受影响");
        _queue.Items.Should().HaveCount(2);
        _queue.Items.Select(i => i.FullPath).Should().BeEquivalentTo(new[] { src1, src2 });
    }

    [Fact(DisplayName = "批量重试：无 Failed 记录返回 0/0")]
    public async Task RescanAllFailed_NoFailed_ReturnsZero()
    {
        SeedItem(MediaItemStatus.Completed);

        BatchRescanResult r = await _sut.RescanAllFailedAsync();

        r.Requeued.Should().Be(0);
        r.Skipped.Should().Be(0);
        _queue.Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "批量重试幂等：二次调用第二次返 0/0，不重复入队")]
    public async Task RescanAllFailed_Twice_SecondCall_IsNoop()
    {
        string src1 = WriteTempFile();
        string src2 = WriteTempFile();
        SeedItemAtFailedWithSource(src1);
        SeedItemAtFailedWithSource(src2);

        BatchRescanResult first = await _sut.RescanAllFailedAsync();
        first.Requeued.Should().Be(2);

        BatchRescanResult second = await _sut.RescanAllFailedAsync();
        second.Requeued.Should().Be(0, "首次已把所有 Failed 转 Queued，二次无 Failed 可重投（幂等）");
        second.Skipped.Should().Be(0);

        _queue.Items.Should().HaveCount(2, "二次调用不重复入队");
    }

    // ---------- 删除记录 ----------

    [Fact(DisplayName = "删除：终态记录连同处理时间线级联删除")]
    public async Task Delete_Terminal_Removes_Item_And_Cascades_Steps()
    {
        long id = SeedCompletedWithStep(WriteTempFile());
        CountSteps(id).Should().Be(1, "前置：已 seed 1 条时间线");

        HistoryBatchResult r = await _sut.DeleteAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Total.Should().Be(1);
        r.Succeeded.Should().ContainSingle().Which.Should().Be(id);
        r.Failed.Should().BeEmpty();
        ItemExists(id).Should().BeFalse("记录已删");
        CountSteps(id).Should().Be(0, "Process_Step 应随主记录级联删除");
    }

    [Fact(DisplayName = "删除：在途/非终态记录拒绝，记录保留")]
    public async Task Delete_NonTerminal_Rejected_And_Kept()
    {
        long id = SeedItem(MediaItemStatus.Queued);

        HistoryBatchResult r = await _sut.DeleteAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Succeeded.Should().BeEmpty();
        r.Failed.Should().ContainSingle().Which.Message.Should().Contain("正在处理或待确认");
        ItemExists(id).Should().BeTrue("被拒绝的记录不应删除");
    }

    [Fact(DisplayName = "删除：空选择器抛 BusinessException")]
    public async Task Delete_EmptySelector_Throws()
    {
        Func<Task> act = async () => await _sut.DeleteAsync(new HistoryBatchRequest());
        await act.Should().ThrowAsync<BusinessException>().WithMessage("未指定要操作的记录");
    }

    [Fact(DisplayName = "删除：批量部分失败——终态删成功 + 在途记入 Failed")]
    public async Task Delete_Batch_PartialFailure_ReportsBoth()
    {
        long ok = SeedItem(MediaItemStatus.Completed);
        long busy = SeedItem(MediaItemStatus.Parsing);

        HistoryBatchResult r = await _sut.DeleteAsync(new HistoryBatchRequest(Ids: new[] { ok, busy }));

        r.Total.Should().Be(2);
        r.Succeeded.Should().ContainSingle().Which.Should().Be(ok);
        r.Failed.Should().ContainSingle().Which.Id.Should().Be(busy);
        ItemExists(ok).Should().BeFalse();
        ItemExists(busy).Should().BeTrue();
    }

    // ---------- 重新处理 ----------

    [Fact(DisplayName = "重新处理：终态记录删旧并以 Manual 重投源文件")]
    public async Task Reprocess_Terminal_Deletes_Old_And_Enqueues_Fresh()
    {
        string src = WriteTempFile();
        long id = SeedCompletedWithStep(src);

        HistoryBatchResult r = await _sut.ReprocessAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Succeeded.Should().ContainSingle().Which.Should().Be(id);
        ItemExists(id).Should().BeFalse("旧记录已删，重投后由 ProcessFileService 全新建项");
        CountSteps(id).Should().Be(0, "旧时间线级联删除");
        _queue.Items.Should().ContainSingle();
        _queue.Items[0].FullPath.Should().Be(src);
        _queue.Items[0].Source.Should().Be(PendingFileSource.Manual);
    }

    [Fact(DisplayName = "重新处理：源文件缺失则失败，旧记录保留、不入队")]
    public async Task Reprocess_MissingSource_Fails_And_Keeps_Record()
    {
        string ghost = Path.Combine(Path.GetTempPath(), $"pmm-h-{Guid.NewGuid():N}.mkv");
        long id = SeedItem(MediaItemStatus.Completed, sourcePath: ghost);

        HistoryBatchResult r = await _sut.ReprocessAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Succeeded.Should().BeEmpty();
        r.Failed.Should().ContainSingle().Which.Message.Should().Contain("源文件已不存在");
        ItemExists(id).Should().BeTrue("失败不应删除记录");
        _queue.Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "重新处理：非终态记录拒绝")]
    public async Task Reprocess_NonTerminal_Rejected()
    {
        long id = SeedItem(MediaItemStatus.Queued);

        HistoryBatchResult r = await _sut.ReprocessAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Failed.Should().ContainSingle().Which.Message.Should().Contain("正在处理或待确认");
        ItemExists(id).Should().BeTrue();
    }

    [Fact(DisplayName = "重新处理：Completed 且目标文件仍在 → 拒绝并引导先撤销归档（防降级 Skipped / 重复文件）")]
    public async Task Reprocess_Completed_TargetStillExists_Rejected()
    {
        string src = WriteTempFile(); // Copy 归档场景：源副本仍在
        long id = SeedCompletedWithTarget(src, "/Media/X (2010)/X (2010).mkv");
        _fileProbe.FileExists("/Media/X (2010)/X (2010).mkv").Returns(true);

        HistoryBatchResult r = await _sut.ReprocessAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Succeeded.Should().BeEmpty();
        r.Failed.Should().ContainSingle().Which.Message.Should().Contain("请先撤销归档");
        ItemExists(id).Should().BeTrue("被拒绝的记录不应删除（避免媒体库视图凭空掉记录）");
        _queue.Items.Should().BeEmpty("拒绝项不应重投");
    }

    [Fact(DisplayName = "重新处理：Completed 但目标文件已被外部删除 → 照常放行重投")]
    public async Task Reprocess_Completed_TargetGone_Allowed()
    {
        string src = WriteTempFile();
        long id = SeedCompletedWithTarget(src, "/Media/X (2010)/X (2010).mkv");
        _fileProbe.FileExists("/Media/X (2010)/X (2010).mkv").Returns(false);

        HistoryBatchResult r = await _sut.ReprocessAsync(new HistoryBatchRequest(Ids: new[] { id }));

        r.Succeeded.Should().ContainSingle().Which.Should().Be(id);
        ItemExists(id).Should().BeFalse("旧记录删除后全新重投");
        _queue.Items.Should().ContainSingle();
    }

    // ---------- 整剧（按 TmdbId 展开）----------

    [Fact(DisplayName = "整剧：按 TmdbId 只展开终态记录，在途与他剧不受影响")]
    public async Task Series_ByTmdbId_Expands_TerminalOnly()
    {
        string s1 = WriteTempFile();
        string s2 = WriteTempFile();
        long ep1 = SeedCompletedWithTmdb(999, "tv", s1);
        long ep2 = SeedCompletedWithTmdb(999, "tv", s2);
        long epBusy = SeedTmdbItem(999, "tv", MediaItemStatus.Parsing); // 同剧在途 → 略过
        long other = SeedCompletedWithTmdb(888, "tv", WriteTempFile());  // 他剧 → 不动

        HistoryBatchResult r = await _sut.ReprocessAsync(new HistoryBatchRequest(TmdbId: 999, TmdbMediaType: "tv"));

        r.Total.Should().Be(2, "只展开同 TmdbId 的终态记录");
        r.Succeeded.Should().BeEquivalentTo(new[] { ep1, ep2 });
        ItemExists(ep1).Should().BeFalse();
        ItemExists(ep2).Should().BeFalse();
        ItemExists(epBusy).Should().BeTrue("在途同剧子项不在展开范围");
        ItemExists(other).Should().BeTrue("他剧不受影响");
        _queue.Items.Should().HaveCount(2);
    }

    [Fact(DisplayName = "整剧：无可操作终态记录时抛 BusinessException")]
    public async Task Series_NoTerminalRecords_Throws()
    {
        SeedTmdbItem(777, "tv", MediaItemStatus.Parsing); // 只有在途

        Func<Task> act = async () => await _sut.DeleteAsync(new HistoryBatchRequest(TmdbId: 777));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("该剧集下没有可操作的记录");
    }

    [Fact]
    public void ExtractParsedInfo_Boundaries()
    {
        HistoryService.ExtractParsedInfo(null).Should().Be((null, (int?)null, (int?)null, (int?)null, (int?)null));
        HistoryService.ExtractParsedInfo("""{"title":"X","year":2020}""")
            .Should().Be(("X", (int?)2020, (int?)null, (int?)null, (int?)null));
        HistoryService.ExtractParsedInfo("""{"title":"Show","year":2020,"season":2,"episode":7}""")
            .Should().Be(("Show", (int?)2020, (int?)2, (int?)7, (int?)null));
        // 字符串数字也接受
        HistoryService.ExtractParsedInfo("""{"season":"1","episode":"12"}""")
            .Should().Be((null, (int?)null, (int?)1, (int?)12, (int?)null));
        // 双集合并：episodeEnd 透出
        HistoryService.ExtractParsedInfo("""{"title":"S","year":2026,"season":1,"episode":8,"episodeEnd":9}""")
            .Should().Be(("S", (int?)2026, (int?)1, (int?)8, (int?)9));
    }

    // ---------- GetDetail ----------

    [Fact]
    public async Task GetDetail_NotFound_Throws()
    {
        Func<Task> act = async () => await _sut.GetDetailAsync(99999);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("记录不存在");
    }

    [Fact]
    public async Task GetDetail_Returns_Tmdb_And_AiCalls()
    {
        long catId = SeedCategory("国产剧");

        // 一次性 fixture 构造 Completed 记录（含 TmdbId / MediaType），避免事后 RebindTmdb（终态保护）
        long id;
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            MediaItem item = MediaItem.CreateFixture(
                $"/tmp/{Guid.NewGuid():N}.mkv", "繁花.S01E03.mkv", 1024,
                status: MediaItemStatus.Completed,
                tmdbId: 200000,
                tmdbMediaType: "tv",
                parseSource: ParseSource.Ai,
                categoryId: catId,
                parsedInfo: """{"title":"繁花","year":2023,"type":"tv","season":1,"episode":3}""");
            db.MediaItems.Add(item);
            db.SaveChanges();
            id = item.Id;
        }

        // 关联 TMDB 元数据 + 预置 AI Provider（FK 约束 Audit_AiCall→Parse_AiProvider）
        long providerA, providerB;
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            ParseAiProvider pa = new() { Name = "qwen", Type = AiProviderType.OpenAiCompatible, BaseUrl = "https://x", Model = "qwen-plus", IsPrimary = true };
            ParseAiProvider pb = new() { Name = "ollama", Type = AiProviderType.Ollama, BaseUrl = "http://x", Model = "llama3", Priority = 10 };
            db.ParseAiProviders.AddRange(pa, pb);
            db.TmdbMetadataCaches.Add(new TmdbMetadataCache
            {
                TmdbId = 200000,
                MediaType = "tv",
                Title = "繁花",
                OriginalTitle = "Blossoms Shanghai",
                Year = 2023,
                CachedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
            providerA = pa.Id;
            providerB = pb.Id;
        }
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            db.AuditAiCalls.Add(new AuditAiCall
            {
                ProviderId = providerA,
                MediaItemId = id,
                Success = true,
                LatencyMs = 1240,
                Timestamp = DateTimeOffset.UtcNow,
            });
            db.AuditAiCalls.Add(new AuditAiCall
            {
                ProviderId = providerB,
                MediaItemId = id,
                Success = false,
                LatencyMs = 5000,
                ErrorType = "Timeout",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(1),
            });
            db.SaveChanges();
        }

        MediaItemDetailResponse detail = await _sut.GetDetailAsync(id);

        detail.Id.Should().Be(id);
        detail.FileName.Should().Be("繁花.S01E03.mkv");
        detail.Status.Should().Be(MediaItemStatus.Completed);
        detail.Category.Should().Be("国产剧");
        detail.TmdbId.Should().Be(200000);
        detail.TmdbMediaType.Should().Be("tv");
        detail.Tmdb.Should().NotBeNull();
        detail.Tmdb!.Title.Should().Be("繁花");
        detail.Tmdb.Year.Should().Be(2023);
        detail.AiCalls.Should().HaveCount(2, "MediaItemId 关联 2 条 AI 调用");
        detail.AiCalls[0].Success.Should().BeTrue();
        detail.AiCalls[1].ErrorType.Should().Be("Timeout");
    }

    [Fact]
    public async Task GetDetail_NoTmdbCache_LeavesTmdbNull()
    {
        long id = SeedItem(MediaItemStatus.Failed);
        MediaItemDetailResponse detail = await _sut.GetDetailAsync(id);
        detail.Tmdb.Should().BeNull();
        detail.AiCalls.Should().BeEmpty();
    }

    // ---------- 测试路径（PreviewArchive）----------

    [Fact]
    public async Task PreviewArchive_HappyPath_Computes_Target_And_CanArchive()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"盗梦空间","year":2010,"type":"movie"}""", "/tmp/inception.mkv");
        _fileProbe.FileExists("/tmp/inception.mkv").Returns(true);

        ArchivePreviewResult r = await _sut.PreviewArchiveAsync(id);

        r.Error.Should().BeNull();
        r.CanArchive.Should().BeTrue();
        r.RelativePath.Should().NotBeNullOrEmpty();
        r.TargetPath.Should().NotBeNullOrEmpty();
        r.TargetExists.Should().BeFalse();
        r.Checks.Should().Contain(c => c.Name == "Plex 命名校验" && c.Ok);
    }

    [Fact]
    public async Task PreviewArchive_Surfaces_Naming_Error_For_Bad_EpisodeRange()
    {
        // episodeEnd(2) < episode(9)：TvEpisodeFileBase 抛 ArgumentOutOfRangeException —— 「最后一部路径错误」精确成因
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 1396, "tv",
            """{"title":"绝命毒师","year":2008,"type":"tv","season":1,"episode":9,"episodeEnd":2}""", "/tmp/bb.mkv");
        _fileProbe.FileExists(Arg.Any<string>()).Returns(true);

        ArchivePreviewResult r = await _sut.PreviewArchiveAsync(id);

        r.CanArchive.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
        r.RelativePath.Should().BeNull();
        r.Checks.Should().Contain(c => c.Name == "Plex 命名校验" && !c.Ok);
    }

    [Fact]
    public async Task PreviewArchive_Flags_Missing_Source_And_Category()
    {
        // 未分类 + 源文件缺失：体检逐项标红，CanArchive=false
        long id = SeedFailedFullMeta(categoryId: null, tmdbId: null, mediaType: null, "{}", "/tmp/missing.mkv");
        _fileProbe.FileExists(Arg.Any<string>()).Returns(false);

        ArchivePreviewResult r = await _sut.PreviewArchiveAsync(id);

        r.CanArchive.Should().BeFalse();
        r.SourceExists.Should().BeFalse();
        r.Checks.Should().Contain(c => c.Name == "源文件存在" && !c.Ok);
        r.Checks.Should().Contain(c => c.Name == "已配置分类目标根" && !c.Ok);
    }

    [Fact]
    public async Task PreviewArchive_NotFound_Throws()
    {
        Func<Task> act = () => _sut.PreviewArchiveAsync(999999);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*不存在*");
    }

    [Fact(DisplayName = "测试路径：标题走 TMDB 回退链 + {tmdb-NNN} 复用既有目录，冲突体检探测真实落点")]
    public async Task PreviewArchive_Uses_TmdbTitle_And_Reuses_TmdbMarkedFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pmm-prev-{Guid.NewGuid():N}");
        string existingFolder = "Inception 旧目录 (2010) {tmdb-27205}";
        Directory.CreateDirectory(Path.Combine(root, existingFolder));
        try
        {
            long catId = SeedCategoryWithRoot(root);
            SeedTmdbCache(27205, "movie", title: "盗梦空间", originalTitle: "Inception");
            long id = SeedFailedFullMeta(catId, 27205, "movie",
                """{"title":"Inception.2010.BluRay","year":2010,"type":"movie"}""", "/tmp/inception.mkv");
            string expectedRelative = Path.Combine(existingFolder, "盗梦空间 (2010) {tmdb-27205}.mkv");
            _fileProbe.FileExists("/tmp/inception.mkv").Returns(true);
            // 目标位已有同名文件 → 体检应在「统一路径计算后的真实落点」上探测，而非旧拼接路径
            _fileProbe.FileExists(Path.Combine(root, expectedRelative)).Returns(true);

            ArchivePreviewResult r = await _sut.PreviewArchiveAsync(id);

            r.CanArchive.Should().BeTrue("同名冲突属软提示，不阻断");
            r.Title.Should().Be("盗梦空间", "标题段用 TMDB 回退链（meta.Title → OriginalTitle → 解析名）");
            r.RelativePath.Should().Be(expectedRelative, "按 {tmdb-NNN} 复用既有目录 + TMDB 规范名文件名，预览=实际落点");
            r.TargetPath.Should().Be(Path.Combine(root, expectedRelative));
            r.TargetExists.Should().BeTrue("冲突体检探测统一路径计算后的真实落点");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact(DisplayName = "测试路径：多季剧集带季首播年与季标题，与实际归档同构")]
    public async Task PreviewArchive_Tv_MultiSeason_Uses_SeasonYear_And_SeasonTitle()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pmm-prev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            long catId = SeedCategoryWithRoot(root);
            string rawJson = """{"seasons":[{"season_number":1,"air_date":"2008-01-20","name":"Season 1"},{"season_number":2,"air_date":"2009-03-08","name":"锻刀村篇"}]}""";
            SeedTmdbCache(1396, "tv", title: "绝命毒师", rawJson: rawJson);
            long id = SeedFailedFullMeta(catId, 1396, "tv",
                """{"title":"Breaking.Bad","year":2008,"type":"tv","season":2,"episode":3}""", "/tmp/bb.mkv");
            _fileProbe.FileExists("/tmp/bb.mkv").Returns(true);

            ArchivePreviewResult r = await _sut.PreviewArchiveAsync(id);

            r.CanArchive.Should().BeTrue();
            r.RelativePath.Should().Be(
                Path.Combine("绝命毒师 (2008) {tmdb-1396}", "Season 02 锻刀村篇", "绝命毒师 (2009) - S02E03.mkv"),
                "季目录带季标题、单集文件名用该季首播年（与 ArchiveService 同一 ArchiveFolderResolver）");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---------- 手动移动（ManualArchive）----------

    [Fact]
    public async Task ManualArchive_Move_HappyPath_Transitions_To_Completed()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"盗梦空间","year":2010,"type":"movie"}""", "/tmp/inception.mkv");
        _fileProbe.FileExists("/tmp/inception.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), ArchiveOperation.Move, Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv", ArchiveOutcome.Completed));

        ManualArchiveResult r = await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));

        r.Status.Should().Be(MediaItemStatus.Completed);
        r.Outcome.Should().Be("Completed");
        r.TargetPath.Should().Be("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv");
        r.Warnings.Should().BeNull("无降级警告时不填");
        MediaItem saved = ReadItem(id);
        saved.Status.Should().Be(MediaItemStatus.Completed);
        saved.TargetPath.Should().Be("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv");
        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), ArchiveOperation.Move, Arg.Any<CancellationToken>());
        // 时间线补了归档 + 完成两步
        CountSteps(id).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ManualArchive_Copy_Passes_Copy_Operation()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), ArchiveOperation.Copy, Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.Completed));

        await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Copy"));

        await _archive.Received(1).ArchiveAsync(Arg.Any<MediaItem>(), ArchiveOperation.Copy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualArchive_Conflict_Transitions_To_Skipped()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.ConflictSkipped));

        ManualArchiveResult r = await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));

        r.Status.Should().Be(MediaItemStatus.Skipped);
        r.Outcome.Should().Be("Skipped");
        ReadItem(id).Status.Should().Be(MediaItemStatus.Skipped);
    }

    [Fact]
    public async Task ManualArchive_Rejects_NonEligible_Status()
    {
        long id = SeedItem(MediaItemStatus.Completed);
        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*不支持手动移动*");
    }

    [Fact]
    public async Task ManualArchive_Rejects_When_Missing_Tmdb()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, tmdbId: null, mediaType: null, """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*尚未匹配 TMDB*");
    }

    [Fact]
    public async Task ManualArchive_Rejects_When_Source_Missing()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/missing.mkv");
        _fileProbe.FileExists("/tmp/missing.mkv").Returns(false);
        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*源文件已不存在*");
    }

    [Fact]
    public async Task ManualArchive_ArchiveThrows_MarksFailed_And_Rethrows()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Throws(new BusinessException("目标磁盘空间不足"));

        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*磁盘空间不足*");
        ReadItem(id).Status.Should().Be(MediaItemStatus.Failed);
    }

    [Fact]
    public async Task ManualArchive_Rejects_Unknown_Mode()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Teleport"));
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*未知的移动模式*");
    }

    [Fact(DisplayName = "手动移动：用户中断 → 记录收尾 Failed 不卡 Archiving，取消照常透传")]
    public async Task ManualArchive_Cancelled_Finalizes_Failed_And_Rethrows()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        using CancellationTokenSource cts = new();
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns<ArchiveResult>(_ =>
            {
                cts.Cancel(); // 模拟移动文件期间用户中断请求
                throw new OperationCanceledException(cts.Token);
            });

        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("真取消必须透传给上层");
        MediaItem saved = ReadItem(id);
        saved.Status.Should().Be(MediaItemStatus.Failed, "中断后记录必须收尾到可恢复终态，不得停留在 Archiving");
        saved.ErrorMessage.Should().Contain("中断");
    }

    [Fact(DisplayName = "手动移动：失败与取消同时发生 → 失败补偿不受已取消令牌影响")]
    public async Task ManualArchive_FailureCompensation_Survives_CancelledToken()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        using CancellationTokenSource cts = new();
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns<ArchiveResult>(_ =>
            {
                cts.Cancel(); // 失败瞬间请求恰被取消：旧实现用已取消 ct 落库 → 补偿被取消打断、记录永卡 Archiving
                throw new IOException("网络盘断开");
            });

        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"), cts.Token);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*手动移动失败*");
        ReadItem(id).Status.Should().Be(MediaItemStatus.Failed, "补偿落库固定用 CancellationToken.None，取消不打断失败收尾");
    }

    [Fact(DisplayName = "手动移动：nfo / Webhook 降级警告透出到结果 Warnings")]
    public async Task ManualArchive_DegradedWarnings_Propagated_To_Result()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.Completed,
                new[] { "元数据(nfo)写入失败：网络盘只读" }));

        ManualArchiveResult r = await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));

        r.Outcome.Should().Be("Completed");
        r.Status.Should().Be(MediaItemStatus.Completed, "降级警告不回退完成判定");
        r.Warnings.Should().ContainSingle().Which.Should().Contain("nfo");
    }

    [Fact(DisplayName = "手动移动失败 → 发 media.failed Webhook（口径对齐自动管线）")]
    public async Task ManualArchive_Failure_Emits_MediaFailed_Webhook()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Throws(new BusinessException("目标磁盘空间不足"));

        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));

        await act.Should().ThrowAsync<BusinessException>();
        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "手动移动冲突跳过 → 发 media.skipped Webhook")]
    public async Task ManualArchive_ConflictSkipped_Emits_MediaSkipped_Webhook()
    {
        long catId = SeedCategoryWithRoot("/Media");
        long id = SeedFailedFullMeta(catId, 27205, "movie", """{"title":"X","year":2010,"type":"movie"}""", "/tmp/x.mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult("/Media/X (2010)/X (2010).mkv", ArchiveOutcome.ConflictSkipped));

        await _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));

        await _webhook.Received(1).EmitAsync(WebhookEvents.MediaSkipped, Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _webhook.DidNotReceive().EmitAsync(WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "手动移动：落 Archiving 同时清空 stale TargetPath，崩溃后启动恢复不误判假完成")]
    public async Task ManualArchive_Clears_Stale_TargetPath_When_Entering_Archiving()
    {
        long catId = SeedCategoryWithRoot("/Media");
        // 冲突跳过落 Skipped 时 TargetPath 记着「目标位他人文件」的路径（stale）
        long id = SeedSkippedFullMetaWithTarget(catId, "/tmp/x.mkv", "/Media/他人文件 (2010).mkv");
        _fileProbe.FileExists("/tmp/x.mkv").Returns(true);
        string? targetPathDuringArchiving = "未读取";
        _archive.ArchiveAsync(Arg.Any<MediaItem>(), Arg.Any<ArchiveOperation>(), Arg.Any<CancellationToken>())
            .Returns<ArchiveResult>(_ =>
            {
                // 此刻 Archiving 在途态已持久化：若 stale TargetPath 未清空，归档中途崩溃后
                // StartupRecoveryWorker 见「TargetPath 文件存在」会把本条误判为 Completed（假完成）
                targetPathDuringArchiving = ReadItem(id).TargetPath;
                throw new IOException("归档中途崩溃");
            });

        Func<Task> act = () => _sut.ManualArchiveAsync(id, new ManualArchiveRequest("Move"));

        await act.Should().ThrowAsync<BusinessException>();
        targetPathDuringArchiving.Should().BeNull("Archiving 在途期间 stale TargetPath 必须已清空");
        MediaItem saved = ReadItem(id);
        saved.Status.Should().Be(MediaItemStatus.Failed);
        saved.TargetPath.Should().BeNull("失败收尾后也不应残留他人文件路径");
    }

    // ---------- 撤销归档（UndoArchive）----------

    [Fact]
    public async Task UndoArchive_MoveBack_HappyPath_MovesFileBack_And_Skips()
    {
        long id = SeedCompletedWithTarget("/dl/inception.mkv", "/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv");
        _fileProbe.FileExists("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv").Returns(true);
        _fileProbe.FileExists("/dl/inception.mkv").Returns(false); // 源已不在 → 当初是 Move，撤销 = 反向 move

        UndoArchiveResult r = await _sut.UndoArchiveAsync(id);

        r.Operation.Should().Be("MOVE_BACK");
        r.RestoredPath.Should().Be("/dl/inception.mkv");
        r.Status.Should().Be(MediaItemStatus.Skipped);
        await _fileMover.Received(1).MoveAsync("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv", "/dl/inception.mkv", Arg.Any<CancellationToken>());
        MediaItem saved = ReadItem(id);
        saved.Status.Should().Be(MediaItemStatus.Skipped);
        saved.TargetPath.Should().BeNull();
        saved.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task UndoArchive_Copy_DeletesArchivedCopy_Without_Moving()
    {
        // 审计链记录 operation=COPY 且源仍在 → 撤销 = 删归档副本，不反向 move
        long id = SeedCompletedWithTarget("/dl/x.mkv", "/Media/X (2010)/X (2010).mkv", archiveOp: "COPY");
        _fileProbe.FileExists("/Media/X (2010)/X (2010).mkv").Returns(true);
        _fileProbe.FileExists("/dl/x.mkv").Returns(true);

        UndoArchiveResult r = await _sut.UndoArchiveAsync(id);

        r.Operation.Should().Be("DELETE_COPY");
        r.Status.Should().Be(MediaItemStatus.Skipped);
        await _fileMover.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        ReadItem(id).Status.Should().Be(MediaItemStatus.Skipped);
    }

    [Fact(DisplayName = "撤销归档：清理该媒体悬空的字幕下载记录，按 MediaItemId 限定不波及其它媒体")]
    public async Task UndoArchive_Removes_SubtitleDownload_Records_Scoped_To_Media()
    {
        // 在线下载字幕落地到归档位旁并记入 Subtitle_Download；撤销归档解除后这些记录的 FileName/TargetPath 必然悬空，应清理
        long id = SeedCompletedWithTarget("/dl/inception.mkv", "/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv");
        long other = SeedCompletedWithTarget("/dl/other.mkv", "/Media/Other (2011)/Other (2011).mkv");
        AddSubtitleDownload(id, "盗梦空间 (2010).zh.srt");
        AddSubtitleDownload(id, "盗梦空间 (2010).en.srt");
        AddSubtitleDownload(other, "Other (2011).zh.srt");
        _fileProbe.FileExists("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv").Returns(true);
        _fileProbe.FileExists("/dl/inception.mkv").Returns(false);

        await _sut.UndoArchiveAsync(id);

        CountSubtitleDownloads(id).Should().Be(0, "归档解除后该媒体的下载记录必须清空，避免 FileName/TargetPath 悬空");
        CountSubtitleDownloads(other).Should().Be(1, "清理须按 MediaItemId 限定，绝不波及其它媒体的下载记录");
    }

    [Fact(DisplayName = "撤销归档（Copy→DELETE_COPY，下载字幕已随副本删除）：同样清理悬空的下载记录")]
    public async Task UndoArchive_Copy_Also_Removes_SubtitleDownload_Records()
    {
        long id = SeedCompletedWithTarget("/dl/x.mkv", "/Media/X (2010)/X (2010).mkv", archiveOp: "COPY");
        AddSubtitleDownload(id, "X (2010).zh.srt");
        _fileProbe.FileExists("/Media/X (2010)/X (2010).mkv").Returns(true);
        _fileProbe.FileExists("/dl/x.mkv").Returns(true); // 源仍在 → DELETE_COPY（下载字幕随副本删除，记录更须清理）

        UndoArchiveResult r = await _sut.UndoArchiveAsync(id);

        r.Operation.Should().Be("DELETE_COPY");
        CountSubtitleDownloads(id).Should().Be(0, "副本（含下载字幕）已删，记录指向已不存在的文件，必须清理");
    }

    [Fact(DisplayName = "退回重改（从已完成）：归档解除，一并清理该媒体的字幕下载记录")]
    public async Task ReopenForReview_FromCompleted_Removes_SubtitleDownload_Records()
    {
        long id = SeedCompletedWithTarget("/dl/x.mkv", "/Media/X (2010)/X (2010).mkv");
        AddSubtitleDownload(id, "X (2010).zh.srt");
        _fileProbe.FileExists("/Media/X (2010)/X (2010).mkv").Returns(true);
        _fileProbe.FileExists("/dl/x.mkv").Returns(false);

        ReopenResult r = await _sut.ReopenForReviewAsync(id);

        r.Status.Should().Be(MediaItemStatus.AwaitingReview);
        CountSubtitleDownloads(id).Should().Be(0, "退回重改解除归档后，下载记录不应残留悬空");
    }

    [Fact]
    public async Task UndoArchive_Rejects_NonCompleted()
    {
        long id = SeedItemAtFailedWithSource("/tmp/x.mkv");
        Func<Task> act = () => _sut.UndoArchiveAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*已完成*");
    }

    [Fact]
    public async Task UndoArchive_Rejects_When_Archived_File_Missing()
    {
        long id = SeedCompletedWithTarget("/dl/x.mkv", "/Media/X.mkv");
        _fileProbe.FileExists("/Media/X.mkv").Returns(false);
        Func<Task> act = () => _sut.UndoArchiveAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*归档文件已不存在*");
    }

    [Fact]
    public async Task UndoArchive_SourceOccupied_Conflict_Throws_And_Keeps_Completed()
    {
        long id = SeedCompletedWithTarget("/dl/x.mkv", "/Media/X.mkv");
        _fileProbe.FileExists("/Media/X.mkv").Returns(true);
        _fileProbe.FileExists("/dl/x.mkv").Returns(false);
        _fileMover.MoveAsync("/Media/X.mkv", "/dl/x.mkv", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileConflictException("/dl/x.mkv"));

        Func<Task> act = () => _sut.UndoArchiveAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*源位置已存在同名文件*");
        ReadItem(id).Status.Should().Be(MediaItemStatus.Completed); // 撤销失败不改状态
    }

    [Fact(DisplayName = "撤销归档：目标文件大小与记录不符（升级替换后他人文件）→ 拒绝且不动文件")]
    public async Task UndoArchive_TargetSizeMismatch_Rejects_And_DoesNotMove()
    {
        string target = WriteTempFileWithBytes(2048); // 目标位实际 2048 字节 ≠ 记录 1024 → 已被新版本替换
        long id = SeedCompletedWithTarget("/dl/x.mkv", target);
        _fileProbe.FileExists(target).Returns(true);

        Func<Task> act = () => _sut.UndoArchiveAsync(id);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*与归档记录不符*");
        await _fileMover.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        ReadItem(id).Status.Should().Be(MediaItemStatus.Completed, "身份校验失败不改状态、不动文件");
    }

    [Fact(DisplayName = "撤销归档：目标文件大小与记录一致 → 身份校验放行")]
    public async Task UndoArchive_TargetSizeMatch_Proceeds()
    {
        string target = WriteTempFileWithBytes(1024); // 与记录 FileSize=1024 一致
        long id = SeedCompletedWithTarget("/dl/x.mkv", target);
        _fileProbe.FileExists(target).Returns(true);
        _fileProbe.FileExists("/dl/x.mkv").Returns(false); // 源不在 → 当初 Move，撤销 = 反向 move

        UndoArchiveResult r = await _sut.UndoArchiveAsync(id);

        r.Operation.Should().Be("MOVE_BACK");
        ReadItem(id).Status.Should().Be(MediaItemStatus.Skipped);
    }

    [Fact(DisplayName = "撤销归档：旧数据 FileSize=0 → 身份校验宽容放行")]
    public async Task UndoArchive_ZeroFileSize_LenientlyPasses()
    {
        string target = WriteTempFileWithBytes(2048);
        long id = SeedCompletedWithTarget("/dl/x.mkv", target, fileSize: 0); // 极早期旧数据未记录大小
        _fileProbe.FileExists(target).Returns(true);
        _fileProbe.FileExists("/dl/x.mkv").Returns(false);

        UndoArchiveResult r = await _sut.UndoArchiveAsync(id);

        r.Operation.Should().Be("MOVE_BACK");
        ReadItem(id).Status.Should().Be(MediaItemStatus.Skipped);
    }

    // ---------- 退回重改（ReopenForReview）----------

    [Fact]
    public async Task Reopen_Completed_MovesFileBack_And_ReturnsToAwaitingReview()
    {
        long id = SeedCompletedWithTarget("/dl/inception.mkv", "/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv");
        _fileProbe.FileExists("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv").Returns(true);
        _fileProbe.FileExists("/dl/inception.mkv").Returns(false); // 源已不在 → 当初是 Move，退回 = 反向 move

        ReopenResult r = await _sut.ReopenForReviewAsync(id);

        r.Operation.Should().Be("MOVE_BACK");
        r.Status.Should().Be(MediaItemStatus.AwaitingReview);
        r.RestoredPath.Should().Be("/dl/inception.mkv");
        await _fileMover.Received(1).MoveAsync("/Media/盗梦空间 (2010)/盗梦空间 (2010).mkv", "/dl/inception.mkv", Arg.Any<CancellationToken>());
        MediaItem saved = ReadItem(id);
        saved.Status.Should().Be(MediaItemStatus.AwaitingReview);
        saved.ReviewReason.Should().Be(ReviewReason.ManualReopen);
        saved.TargetPath.Should().BeNull();
        saved.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reopen_Skipped_DoesNotMoveFile_And_ReturnsToAwaitingReview()
    {
        // 同名冲突落 Skipped：源文件从未移走、仍在源位 → 退回不动文件，仅校验源仍在
        long id = SeedItem(MediaItemStatus.Skipped, sourcePath: "/dl/conflict.mkv");
        _fileProbe.FileExists("/dl/conflict.mkv").Returns(true);

        ReopenResult r = await _sut.ReopenForReviewAsync(id);

        r.Operation.Should().Be("REOPEN_INPLACE");
        r.Status.Should().Be(MediaItemStatus.AwaitingReview);
        await _fileMover.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        MediaItem saved = ReadItem(id);
        saved.Status.Should().Be(MediaItemStatus.AwaitingReview);
        saved.ReviewReason.Should().Be(ReviewReason.ManualReopen);
    }

    [Fact]
    public async Task Reopen_Skipped_SourceMissing_Throws()
    {
        long id = SeedItem(MediaItemStatus.Skipped, sourcePath: "/dl/gone.mkv");
        _fileProbe.FileExists("/dl/gone.mkv").Returns(false); // 源也没了，退回无意义

        Func<Task> act = () => _sut.ReopenForReviewAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*源文件已不存在*");
    }

    [Fact]
    public async Task Reopen_Completed_ArchivedFileMissing_Throws()
    {
        long id = SeedCompletedWithTarget("/dl/x.mkv", "/Media/X.mkv");
        _fileProbe.FileExists("/Media/X.mkv").Returns(false); // 归档文件被外部删 / 移走

        Func<Task> act = () => _sut.ReopenForReviewAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*归档文件已不存在*");
    }

    [Fact]
    public async Task Reopen_Rejects_NonConfirmTerminal()
    {
        long id = SeedItemAtFailedWithSource("/tmp/x.mkv"); // Failed 不是 confirm 落点
        Func<Task> act = () => _sut.ReopenForReviewAsync(id);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("*已完成*已跳过*");
    }

    [Fact(DisplayName = "退回重改：目标文件大小与记录不符（升级替换后他人文件）→ 拒绝且不动文件")]
    public async Task Reopen_TargetSizeMismatch_Rejects_And_DoesNotMove()
    {
        string target = WriteTempFileWithBytes(4096); // 目标位实际 4096 字节 ≠ 记录 1024
        long id = SeedCompletedWithTarget("/dl/x.mkv", target);
        _fileProbe.FileExists(target).Returns(true);

        Func<Task> act = () => _sut.ReopenForReviewAsync(id);

        await act.Should().ThrowAsync<BusinessException>().WithMessage("*与归档记录不符*");
        await _fileMover.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        ReadItem(id).Status.Should().Be(MediaItemStatus.Completed, "身份校验失败不改状态、不动文件");
    }

    /// <summary>seed 一条 Completed 记录并带 TargetPath，可选附一条 Archiving 时间线（operation=MOVE/COPY）；fileSize 供身份校验场景定制</summary>
    private long SeedCompletedWithTarget(string sourcePath, string targetPath, string? archiveOp = null, long fileSize = 1024)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(sourcePath, Path.GetFileName(sourcePath), fileSize,
            status: MediaItemStatus.Completed, targetPath: targetPath);
        if (archiveOp is not null)
            item.AppendStep(MediaItemStatus.Archiving, DateTimeOffset.UtcNow, 10,
                $$"""{"operation":"{{archiveOp}}","source":"{{sourcePath}}","target":"{{targetPath}}"}""");
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>seed 一条 Skipped 记录（元数据齐全）并带 stale TargetPath（模拟冲突跳过时记下的他人文件路径）</summary>
    private long SeedSkippedFullMetaWithTarget(long categoryId, string sourcePath, string staleTargetPath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(sourcePath, Path.GetFileName(sourcePath), 1024,
            status: MediaItemStatus.Skipped,
            tmdbId: 27205, tmdbMediaType: "movie", categoryId: categoryId,
            parsedInfo: """{"title":"X","year":2010,"type":"movie"}""",
            targetPath: staleTargetPath);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>seed 一条 TMDB 元数据缓存（预览标题回退链 / 多季 RawJson 场景用）</summary>
    private void SeedTmdbCache(int tmdbId, string mediaType, string? title = null, string? originalTitle = null, string? rawJson = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.TmdbMetadataCaches.Add(new TmdbMetadataCache
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            OriginalTitle = originalTitle,
            RawJson = rawJson,
            CachedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    // ---------- helpers ----------

    private long SeedCategoryWithRoot(string targetRoot)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new() { Name = $"Cat-{Guid.NewGuid():N}", MediaType = MediaType.Both, TargetRoot = targetRoot };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    /// <summary>seed 一条 Failed 记录并带齐（可选）分类 / TMDB / ParsedInfo（手动移动 / 测试路径用）</summary>
    private long SeedFailedFullMeta(long? categoryId, int? tmdbId, string? mediaType, string parsedInfo, string sourcePath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(sourcePath, Path.GetFileName(sourcePath), 1024,
            tmdbId: tmdbId, tmdbMediaType: mediaType, categoryId: categoryId,
            parseSource: ParseSource.Rule, confidence: 0.9, parsedInfo: parsedInfo);
        item.MarkFailed("seed path error");
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private string WriteTempFile()
    {
        string p = Path.Combine(Path.GetTempPath(), $"pmm-h-{Guid.NewGuid():N}.mkv");
        File.WriteAllText(p, "x");
        _tempFiles.Add(p);
        return p;
    }

    /// <summary>写指定字节数的真实临时文件（身份校验按 FileInfo.Length 对比，必须落真实磁盘）</summary>
    private string WriteTempFileWithBytes(int byteCount)
    {
        string p = Path.Combine(Path.GetTempPath(), $"pmm-h-{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(p, new byte[byteCount]);
        _tempFiles.Add(p);
        return p;
    }

    private MediaItem ReadItem(long id)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.MediaItems.AsNoTracking().Single(m => m.Id == id);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        int waited = 0;
        while (!predicate() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
        predicate().Should().BeTrue("条件应在超时前满足");
    }

    /// <summary>给指定媒体加一条字幕下载记录（模拟在线下载落地到归档位旁后写入 Subtitle_Download）</summary>
    private long AddSubtitleDownload(long mediaItemId, string fileName)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SubtitleDownload row = new()
        {
            MediaItemId = mediaItemId,
            Provider = SubtitleProviderType.Assrt,
            SubtitleName = "候选名",
            FileName = fileName,
            TargetPath = $"/Media/X (2010)/{fileName}",
            Language = "zh",
            FileSize = 100,
        };
        db.SubtitleDownloads.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    /// <summary>查某媒体当前的字幕下载记录数</summary>
    private int CountSubtitleDownloads(long mediaItemId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.SubtitleDownloads.AsNoTracking().Count(d => d.MediaItemId == mediaItemId);
    }

    private long SeedCategory(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new() { Name = name, MediaType = MediaType.Both, TargetRoot = "/M" };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    private long SeedItem(MediaItemStatus status, ParseSource? source = null, long? categoryId = null, string? sourcePath = null)
        => SeedItemWithParsedInfo(status, fileName: null, parsedInfo: "{}", source, categoryId, sourcePath);

    private long SeedItemWithParsedInfo(MediaItemStatus status, string? fileName, string parsedInfo,
        ParseSource? source = null, long? categoryId = null, string? sourcePath = null)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        string path = sourcePath ?? $"/tmp/{Guid.NewGuid():N}.mkv";
        string fn = fileName ?? Path.GetFileName(path);

        MediaItem item;
        if (status == MediaItemStatus.Failed)
        {
            item = MediaItem.CreateFixture(path, fn, 1024,
                parseSource: source, categoryId: categoryId, parsedInfo: parsedInfo);
            item.MarkFailed("seed");
        }
        else
        {
            item = MediaItem.CreateFixture(path, fn, 1024,
                status: status, parseSource: source, categoryId: categoryId, parsedInfo: parsedInfo);
        }

        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private long SeedWatchFolder(string path)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        Domain.Aggregates.WatchDirectories.WatchFolder w = new()
        {
            Path = path,
            Alias = Path.GetFileName(path),
            Enabled = true,
        };
        db.WatchFolders.Add(w);
        db.SaveChanges();
        return w.Id;
    }

    /// <summary>seed 一条 Completed 记录并附带 1 条处理时间线（验证级联删用）</summary>
    private long SeedCompletedWithStep(string sourcePath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(sourcePath, Path.GetFileName(sourcePath), 1024,
            status: MediaItemStatus.Completed);
        item.AppendStep(MediaItemStatus.Completed, DateTimeOffset.UtcNow, 5);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>seed 一条带 TmdbId 的 Completed 记录（整剧展开用）</summary>
    private long SeedCompletedWithTmdb(int tmdbId, string mediaType, string sourcePath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(sourcePath, Path.GetFileName(sourcePath), 1024,
            status: MediaItemStatus.Completed, tmdbId: tmdbId, tmdbMediaType: mediaType);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>seed 一条带 TmdbId 的任意状态记录（含在途态，整剧筛选用）</summary>
    private long SeedTmdbItem(int tmdbId, string mediaType, MediaItemStatus status)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture($"/tmp/{Guid.NewGuid():N}.mkv", "ep.mkv", 1024,
            status: status, tmdbId: tmdbId, tmdbMediaType: mediaType);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private int CountSteps(long mediaItemId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.ProcessSteps.AsNoTracking().Count(s => s.MediaItemId == mediaItemId);
    }

    private bool ItemExists(long id)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.MediaItems.AsNoTracking().Any(m => m.Id == id);
    }

    private long SeedItemAtFailedWithSource(string sourcePath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateDetected(sourcePath, Path.GetFileName(sourcePath), 1024);
        item.MarkFailed("disk full");
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private void SetUpdatedAt(long id, DateTimeOffset newValue)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Where(m => m.Id == id)
            .ExecuteUpdate(s => s.SetProperty(m => m.UpdatedAt, newValue));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            opts.AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }

    private sealed class InMemoryQueue : IPendingFileQueue
    {
        public List<PendingFileItem> Items { get; } = new();
        public ValueTask EnqueueAsync(PendingFileItem item, CancellationToken cancellationToken = default)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
        public System.Threading.Channels.ChannelReader<PendingFileItem> Reader => throw new NotImplementedException();
    }
}
