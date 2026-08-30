using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Review;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>StartupRecoveryWorker（P1-r2.5）— 修复 Archiving 孤儿状态</summary>
/// <remarks>
/// 验证场景：
/// 1. Archiving + TargetPath 文件存在且长度吻合 → 恢复为 Completed
/// 2. Archiving + TargetPath 不存在          → 恢复为 Failed + 发 media.failed + 时间线步骤
/// 3. Archiving + TargetPath 存在但长度不符   → 恢复为 Failed（防 stale 路径假 Completed）
/// 4. Archiving + TargetPath=null 但重算落点已落地 → 补 TargetPath 并恢复为 Completed（防源已删误判 Failed）
/// 5. 其他状态（Completed / Failed 等）       → 不改动
/// 6. 在途记录重排回 Queued + 重新入队 + 时间线步骤
/// </remarks>
public sealed class StartupRecoveryWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _tempDir;

    public StartupRecoveryWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _tempDir = Path.Combine(Path.GetTempPath(), $"pmm-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 1：目标文件存在且长度吻合 → Completed
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archiving_TargetExists_RecoveredToCompleted()
    {
        // 创建目标文件（模拟归档已物理完成；长度与 FileSize=1024 吻合）
        string targetPath = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllBytesAsync(targetPath, new byte[1024]);

        long id = SeedArchivingItem(targetPath);

        WorkerHarness h = await RunWorkerAsync();

        MediaItem result = ReadItem(id);
        result.Status.Should().Be(MediaItemStatus.Completed,
            "目标文件已存在且长度吻合说明归档操作实际完成，应推进为 Completed");
        result.ArchivedAt.Should().NotBeNull("Completed 转移时 ArchivedAt 应被填充");
        ReadSteps(id).Should().Contain(s => s.Stage == MediaItemStatus.Completed,
            "状态改写应落时间线步骤");
        await h.Webhook.DidNotReceive().EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 2：目标文件不存在 → Failed + media.failed + 时间线
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archiving_TargetNotFound_RecoveredToFailed()
    {
        // TargetPath 指向不存在的文件
        string missingPath = Path.Combine(_tempDir, "missing.mkv");
        long id = SeedArchivingItem(missingPath);

        WorkerHarness h = await RunWorkerAsync();

        MediaItem result = ReadItem(id);
        result.Status.Should().Be(MediaItemStatus.Failed,
            "目标文件不存在说明归档未完成，应标记 Failed 供用户 Rescan");
        result.ErrorMessage.Should().Contain("归档中断");
        ReadSteps(id).Should().Contain(s => s.Stage == MediaItemStatus.Failed,
            "判 Failed 应落时间线步骤");
        await h.Webhook.Received(1).EmitAsync(
            WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 3：TargetPath 存在但长度不符（stale 路径）→ Failed
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archiving_TargetExists_SizeMismatch_RecoveredToFailed()
    {
        // 目标文件存在但长度（5 字节）≠ 记录 FileSize（1024）：TargetPath 指向他人文件，不可判假 Completed
        string stalePath = Path.Combine(_tempDir, "stale.mkv");
        await File.WriteAllTextAsync(stalePath, "stale");
        long id = SeedArchivingItem(stalePath);

        WorkerHarness h = await RunWorkerAsync();

        MediaItem result = ReadItem(id);
        result.Status.Should().Be(MediaItemStatus.Failed,
            "目标文件长度与记录不符，应判 Failed 供人工核查而非假 Completed");
        result.ErrorMessage.Should().Contain("目标文件与记录不符");
        await h.Webhook.Received(1).EmitAsync(
            WebhookEvents.MediaFailed, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 4：TargetPath=null 但重算落点文件已落地 → Completed + 补 TargetPath
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archiving_NullTargetPath_FileLanded_RecoveredToCompleted_BackfillsTargetPath()
    {
        // 模拟「ArchiveAsync 已移文件、SetArchiveResult 落库前崩溃」：TargetPath=null 但视频实际已落地
        long categoryId = SeedCategory(_tempDir);
        string landedPath = Path.Combine(_tempDir, "landed.mkv");
        await File.WriteAllBytesAsync(landedPath, new byte[1024]);

        long id = SeedArchivingItemWithMatch(categoryId);

        IReviewService review = Substitute.For<IReviewService>();
        review.PreviewPathsAsync(Arg.Any<ReviewPreviewPathRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ReviewPreviewPathResult(new[]
            {
                new ReviewPreviewPathEntry(
                    ci.Arg<ReviewPreviewPathRequest>().Items[0].Key, "rel/landed.mkv", landedPath, null),
            }));

        WorkerHarness h = await RunWorkerAsync(review);

        MediaItem result = ReadItem(id);
        result.Status.Should().Be(MediaItemStatus.Completed,
            "重算预期落点的文件已存在且长度吻合，应按 Completed 收尾而非误判 Failed");
        result.TargetPath.Should().Be(landedPath, "应补写崩溃前未落库的 TargetPath");
        ReadSteps(id).Should().Contain(s => s.Stage == MediaItemStatus.Completed);
        await h.Webhook.DidNotReceive().EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Archiving_NullTargetPath_LandedFileSizeMismatch_RecoveredToFailed()
    {
        // 重算落点有文件但长度不符（他人文件 / 冲突遗留）→ 不可补 Completed，仍判 Failed
        long categoryId = SeedCategory(_tempDir);
        string occupiedPath = Path.Combine(_tempDir, "occupied.mkv");
        await File.WriteAllTextAsync(occupiedPath, "other"); // 5 字节 ≠ 1024

        long id = SeedArchivingItemWithMatch(categoryId);

        IReviewService review = Substitute.For<IReviewService>();
        review.PreviewPathsAsync(Arg.Any<ReviewPreviewPathRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ReviewPreviewPathResult(new[]
            {
                new ReviewPreviewPathEntry(
                    ci.Arg<ReviewPreviewPathRequest>().Items[0].Key, "rel/occupied.mkv", occupiedPath, null),
            }));

        await RunWorkerAsync(review);

        ReadItem(id).Status.Should().Be(MediaItemStatus.Failed,
            "重算落点文件长度与记录不符，不能据此判 Completed");
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 5：TargetPath 为 null 且字段不全（无法重算）→ Failed
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archiving_NullTargetPath_RecoveredToFailed()
    {
        // TargetPath 为 null（archive 连 TargetPath 都没来得及写）且缺 TMDB/分类信息无法重算落点
        long id = SeedArchivingItem(targetPath: null);

        await RunWorkerAsync();

        MediaItem result = ReadItem(id);
        result.Status.Should().Be(MediaItemStatus.Failed);
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 6：已是终态 → 不改动
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NonArchivingStatus_NotTouched()
    {
        long completedId = SeedItem(MediaItemStatus.Completed, "/done.mkv");
        long failedId    = SeedItem(MediaItemStatus.Failed,    "/fail.mkv");

        await RunWorkerAsync();

        ReadItem(completedId).Status.Should().Be(MediaItemStatus.Completed, "终态不应被恢复器改动");
        ReadItem(failedId).Status.Should().Be(MediaItemStatus.Failed);
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 7：多条孤儿 → 全部恢复
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Multiple_Archiving_AllRecovered()
    {
        string existPath = Path.Combine(_tempDir, "exists.mkv");
        await File.WriteAllBytesAsync(existPath, new byte[1024]);

        long id1 = SeedArchivingItem(existPath);          // → Completed
        long id2 = SeedArchivingItem("/nonexistent.mkv"); // → Failed
        long id3 = SeedArchivingItem(null);               // → Failed

        await RunWorkerAsync();

        ReadItem(id1).Status.Should().Be(MediaItemStatus.Completed);
        ReadItem(id2).Status.Should().Be(MediaItemStatus.Failed);
        ReadItem(id3).Status.Should().Be(MediaItemStatus.Failed);
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 8：Queued 在途记录 → 保持 Queued 并重新入队（+ 时间线步骤）
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueuedItem_StaysQueued_AndReenqueued()
    {
        long id = SeedWithStatus(MediaItemStatus.Queued, @"F:\dl\show\01.mkv");

        WorkerHarness h = await RunWorkerAsync();

        ReadItem(id).Status.Should().Be(MediaItemStatus.Queued, "Queued 在途记录保持 Queued");
        h.Queue.Items.Should().ContainSingle(i => i.FullPath == @"F:\dl\show\01.mkv",
            "重启丢失的 Queued 记录应被重新入队");
        ReadSteps(id).Should().Contain(s => s.Stage == MediaItemStatus.Queued,
            "启动重排应落时间线步骤");
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 9：中途态（TmdbMatching）孤儿 → 重置为 Queued 并重新入队
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MidPipelineItem_ResetToQueued_AndReenqueued()
    {
        long id = SeedWithStatus(MediaItemStatus.TmdbMatching, @"F:\dl\show\02.mkv");

        WorkerHarness h = await RunWorkerAsync();

        ReadItem(id).Status.Should().Be(MediaItemStatus.Queued,
            "中途态孤儿应被重置回 Queued 以便从头重跑");
        h.Queue.Items.Should().ContainSingle(i => i.FullPath == @"F:\dl\show\02.mkv");
        ReadSteps(id).Should().Contain(s => s.Stage == MediaItemStatus.Queued,
            "启动重排应落时间线步骤");
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 10：AwaitingReview（等待人工）→ 不重排、不入队
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AwaitingReview_NotRequeued()
    {
        long id = SeedWithStatus(MediaItemStatus.AwaitingReview, @"F:\dl\show\03.mkv");

        WorkerHarness h = await RunWorkerAsync();

        ReadItem(id).Status.Should().Be(MediaItemStatus.AwaitingReview, "等待人工的记录不应被重排");
        h.Queue.Items.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────
    // 场景 11：WatchFolderId 按 SourcePath 前缀推导
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Requeue_ResolvesWatchFolderId_ByLongestPrefix()
    {
        SeedWatchFolder(@"F:\dl");
        long deepId = SeedWatchFolder(@"F:\dl\show");   // 更长前缀应优先
        SeedWithStatus(MediaItemStatus.Queued, @"F:\dl\show\04.mkv");

        WorkerHarness h = await RunWorkerAsync();

        h.Queue.Items.Should().ContainSingle()
            .Which.WatchFolderId.Should().Be(deepId, "应匹配最长前缀的监控目录");
    }

    [Fact]
    public async Task Requeue_NoWatchFolderMatch_UsesZero()
    {
        SeedWatchFolder(@"F:\dl");
        SeedWithStatus(MediaItemStatus.Queued, @"X:\other\05.mkv");

        WorkerHarness h = await RunWorkerAsync();

        h.Queue.Items.Should().ContainSingle().Which.WatchFolderId.Should().Be(0,
            "无任何监控目录前缀匹配时退化为 0");
    }

    // ──────────────────────────────────────────────────────────────
    // 帮助方法
    // ──────────────────────────────────────────────────────────────

    /// <summary>worker 运行结果句柄：入队记录 + Webhook 替身（断言 media.failed 发送）</summary>
    private sealed record WorkerHarness(RecordingQueue Queue, IWebhookEmitter Webhook);

    private async Task<WorkerHarness> RunWorkerAsync(IReviewService? review = null)
    {
        RecordingQueue queue = new();
        IWebhookEmitter webhook = Substitute.For<IWebhookEmitter>();
        StartupRecoveryWorker sut = new(
            _dbFactory, queue, webhook, new FixedClock(), BuildScopeFactory(review),
            NullLogger<StartupRecoveryWorker>.Instance);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        // r3 P1-r3.14 改为 IHostedService 后，StartAsync 同步阻塞至恢复完成才返回；无需额外延时
        await sut.StartAsync(cts.Token);
        // 重新入队走后台任务，await 它确保入队完成后再断言
        if (sut.RequeueEnqueueTask is not null)
        {
            await sut.RequeueEnqueueTask;
        }
        await sut.StopAsync(CancellationToken.None);
        return new WorkerHarness(queue, webhook);
    }

    /// <summary>构造仅含 Scoped IReviewService 的作用域工厂（worker 内经作用域解析去向预览）</summary>
    private static IServiceScopeFactory BuildScopeFactory(IReviewService? review)
    {
        IReviewService stub = review ?? CreateEmptyReviewStub();
        ServiceCollection services = new();
        services.AddScoped<IReviewService>(_ => stub);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>默认去向预览替身：返回空结果（等价「重算不出落点」，走 Failed 兜底分支）</summary>
    private static IReviewService CreateEmptyReviewStub()
    {
        IReviewService stub = Substitute.For<IReviewService>();
        stub.PreviewPathsAsync(Arg.Any<ReviewPreviewPathRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewPreviewPathResult(Array.Empty<ReviewPreviewPathEntry>()));
        return stub;
    }

    /// <summary>入库一条 Archiving 状态的 MediaItem，TargetPath 已通过 SetArchiveResult 设置</summary>
    private long SeedArchivingItem(string? targetPath)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();

        MediaItem item = MediaItem.CreateDetected(
            $"/src/{Guid.NewGuid():N}.mkv",
            "test.mkv",
            1024);

        // 走合法路径到 Archiving：Detected → Queued → Parsing → TmdbMatching → Classifying → Archiving
        item.Transition(MediaItemStatus.Queued);
        item.Transition(MediaItemStatus.Parsing);
        item.Transition(MediaItemStatus.TmdbMatching);
        item.Transition(MediaItemStatus.Classifying);
        item.Transition(MediaItemStatus.Archiving);

        if (targetPath is not null)
        {
            item.SetArchiveResult(targetPath);
        }

        ctx.MediaItems.Add(item);
        ctx.SaveChanges();
        return item.Id;
    }

    /// <summary>入库一条带 TMDB 匹配 + 分类的 Archiving 孤儿（TargetPath=null，供重算落点场景）</summary>
    private long SeedArchivingItemWithMatch(long categoryId)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();

        MediaItem item = MediaItem.CreateDetected(
            $"/src/{Guid.NewGuid():N}.mkv",
            "landed.mkv",
            1024);

        item.Transition(MediaItemStatus.Queued);
        item.Transition(MediaItemStatus.Parsing);
        item.ApplyTmdbMatch(550, "movie", ParseSource.Rule, 0.95,
            new ParsedInfo("测试电影", 2020, "movie", null, null, null, null));
        item.Transition(MediaItemStatus.TmdbMatching);
        item.Transition(MediaItemStatus.Classifying);
        item.AssignCategory(categoryId);
        item.Transition(MediaItemStatus.Archiving);

        ctx.MediaItems.Add(item);
        ctx.SaveChanges();
        return item.Id;
    }

    /// <summary>入库一条分类定义（MediaItem.CategoryId 有外键约束，重算落点场景需真实分类行）</summary>
    private long SeedCategory(string targetRoot)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        CategoryDefinition cat = new()
        {
            Name = $"电影-{Guid.NewGuid():N}",
            MediaType = MediaType.Movie,
            TargetRoot = targetRoot,
        };
        ctx.CategoryDefinitions.Add(cat);
        ctx.SaveChanges();
        return cat.Id;
    }

    /// <summary>入库一条指定状态的 MediaItem（不要求合法路径，用于终态断言测试）</summary>
    private long SeedItem(MediaItemStatus status, string sourcePath)
    {
        // 构建一条 Archiving 的 item 再强制转到终态
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateDetected(sourcePath, Path.GetFileName(sourcePath), 512);
        item.Transition(MediaItemStatus.Queued);
        item.Transition(MediaItemStatus.Parsing);
        item.Transition(MediaItemStatus.TmdbMatching);
        item.Transition(MediaItemStatus.Classifying);
        item.Transition(MediaItemStatus.Archiving);

        if (status == MediaItemStatus.Completed)
        {
            item.SetArchiveResult("/dummy/target.mkv");
            item.Transition(MediaItemStatus.Completed);
        }
        else if (status == MediaItemStatus.Failed)
        {
            item.MarkFailed("预置失败");
        }

        ctx.MediaItems.Add(item);
        ctx.SaveChanges();
        return item.Id;
    }

    private MediaItem ReadItem(long id)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.MediaItems.AsNoTracking().Single(m => m.Id == id);
    }

    private List<ProcessStep> ReadSteps(long id)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.ProcessSteps.AsNoTracking().Where(s => s.MediaItemId == id).ToList();
    }

    /// <summary>沿合法前向转移把一条 MediaItem 落到指定（在途/AwaitingReview）状态</summary>
    private long SeedWithStatus(MediaItemStatus target, string sourcePath)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateDetected(sourcePath, Path.GetFileName(sourcePath), 1024);
        switch (target)
        {
            case MediaItemStatus.Detected:
                break;
            case MediaItemStatus.Queued:
                item.Transition(MediaItemStatus.Queued);
                break;
            case MediaItemStatus.TmdbMatching:
                item.Transition(MediaItemStatus.Queued);
                item.Transition(MediaItemStatus.Parsing);
                item.Transition(MediaItemStatus.TmdbMatching);
                break;
            case MediaItemStatus.AwaitingReview:
                item.Transition(MediaItemStatus.Queued);
                item.Transition(MediaItemStatus.Parsing);
                item.Transition(MediaItemStatus.TmdbMatching);
                item.MarkAwaitingReview(ReviewReason.TmdbZeroResult);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "测试桩未覆盖该状态");
        }
        ctx.MediaItems.Add(item);
        ctx.SaveChanges();
        return item.Id;
    }

    private long SeedWatchFolder(string path)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        WatchFolder folder = new() { Path = path, Enabled = true };
        ctx.WatchFolders.Add(folder);
        ctx.SaveChanges();
        return folder.Id;
    }

    /// <summary>记录入队项的 IPendingFileQueue 假实现（EnqueueAsync 同步完成，不阻塞）</summary>
    private sealed class RecordingQueue : IPendingFileQueue
    {
        private readonly Channel<PendingFileItem> _channel = Channel.CreateUnbounded<PendingFileItem>();

        public List<PendingFileItem> Items { get; } = new();

        public ValueTask EnqueueAsync(PendingFileItem item, CancellationToken cancellationToken = default)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }

        public ChannelReader<PendingFileItem> Reader => _channel.Reader;
    }

    /// <summary>固定时钟替身（时间线步骤 startedAt 用，不参与断言）</summary>
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
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

        public Task<PmmDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
