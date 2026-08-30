using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>FileWatcherWorker（D6.1）— 启动 / 事件入队 / Enabled 过滤 / 缺目录容错 / Stop 释放 / 信号增量重建</summary>
/// <remarks>
/// 用真实 SQLite in-memory + EnsureCreated 验证 IDbContextFactory 行为，
/// IFileWatcher / IFileWatcherFactory 用 NSubstitute 替身，触发合成 OnFileEvent 验证入队效果。
/// 物理目录用 Path.GetTempPath() 临时目录，避免 worker 走 Directory.Exists==false 分支。
/// 重建信号场景：经真实 WatchRebuildSignal 发布（验证消费循环端到端）或直调 internal
/// HandleRebuildSignalAsync（确定性断言挂载 / 卸载 / 补扫），IScanService 替身经作用域工厂注入。
/// </remarks>
public sealed class FileWatcherWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _existingDir;

    public FileWatcherWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _existingDir = Path.Combine(Path.GetTempPath(), $"pmm-fw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_existingDir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_existingDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task StartAsync_Registers_Watcher_Per_Enabled_Folder()
    {
        string disabledDir = CreateSubDir("disabled");
        SeedFolders(
            (path: _existingDir, enabled: true, priority: 10),
            (path: disabledDir, enabled: false, priority: 5));

        IFileWatcher watcherStub = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcherStub);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        sut.RegisteredCount.Should().Be(1, "Enabled=false 的目录不应被监听");
        factory.Received(1).Create(_existingDir);
        watcherStub.Received(1).Start();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_Skips_Folder_With_AutoScan_Disabled()
    {
        // 自动监控关闭的目录即便 Enabled=true 也不挂实时 watcher（仅手动扫描处理）
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.WatchFolders.Add(new WatchFolder { Path = _existingDir, Enabled = true, AutoScan = false, Priority = 10 });
            ctx.SaveChanges();
        }

        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ => NewStubWatcher());

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue());
        await sut.StartAsync(CancellationToken.None);
        await sut.StartupRegistrationCompleted;

        sut.RegisteredCount.Should().Be(0, "AutoScan=false 的目录不应被实时监听");
        factory.DidNotReceive().Create(_existingDir);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Created_Event_Enqueues_With_Watcher_Source_And_FolderId()
    {
        long folderId = SeedFolder(_existingDir, enabled: true);
        SyntheticWatcher watcher = new();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        string fakeFile = Path.Combine(_existingDir, "newfile.mkv");
        watcher.Fire(new FileWatcherEvent(FileWatcherChangeType.Created, fakeFile, null));

        PendingFileItem item = await ReadOne(queue);
        item.FullPath.Should().Be(fakeFile);
        item.WatchFolderId.Should().Be(folderId);
        item.Source.Should().Be(PendingFileSource.Watcher);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Renamed_Event_Enqueues()
    {
        SeedFolder(_existingDir, enabled: true);
        SyntheticWatcher watcher = new();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        string renamed = Path.Combine(_existingDir, "renamed.mkv");
        watcher.Fire(new FileWatcherEvent(FileWatcherChangeType.Renamed, renamed, "old.mkv"));

        PendingFileItem item = await ReadOne(queue);
        item.FullPath.Should().Be(renamed);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Changed_And_Deleted_Events_DoNotEnqueue()
    {
        SeedFolder(_existingDir, enabled: true);
        SyntheticWatcher watcher = new();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        string anyFile = Path.Combine(_existingDir, "x.mkv");
        watcher.Fire(new FileWatcherEvent(FileWatcherChangeType.Changed, anyFile, null));
        watcher.Fire(new FileWatcherEvent(FileWatcherChangeType.Deleted, anyFile, null));

        // 给入队任务 100ms 机会执行，确保确实没有入队
        await Task.Delay(100);
        queue.Reader.Count.Should().Be(0);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Created_Event_For_Directory_Is_Ignored()
    {
        SeedFolder(_existingDir, enabled: true);
        SyntheticWatcher watcher = new();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        // 实际创建一个子目录让 Directory.Exists==true
        string subDir = Path.Combine(_existingDir, "subdir");
        Directory.CreateDirectory(subDir);
        watcher.Fire(new FileWatcherEvent(FileWatcherChangeType.Created, subDir, null));

        await Task.Delay(100);
        queue.Reader.Count.Should().Be(0);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Missing_Folder_Path_Is_Skipped_Without_Throw()
    {
        string ghostPath = Path.Combine(_existingDir, "no-such");
        SeedFolders(
            (path: ghostPath, enabled: true, priority: 5),
            (path: _existingDir, enabled: true, priority: 10));

        IFileWatcher watcherStub = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcherStub);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);

        Func<Task> act = async () => await sut.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        await WaitUntil(() => sut.RegisteredCount == 1);

        sut.RegisteredCount.Should().Be(1, "不存在的路径跳过，仅存在的路径注册");
        factory.DidNotReceive().Create(ghostPath);
        factory.Received(1).Create(_existingDir);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Folders_Loaded_In_Priority_Ascending()
    {
        string a = CreateSubDir("p30");
        string b = CreateSubDir("p10");
        string c = CreateSubDir("p20");
        SeedFolders(
            (path: a, enabled: true, priority: 30),
            (path: b, enabled: true, priority: 10),
            (path: c, enabled: true, priority: 20));

        List<int> createOrder = new();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ =>
        {
            createOrder.Add(createOrder.Count);
            return NewStubWatcher();
        });
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 3);

        // 验证 DB 加载已按 Priority 升序排好；三次 Create 调用顺序对应 10/20/30
        sut.RegisteredCount.Should().Be(3);
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_Disposes_All_Watchers_And_Clears()
    {
        SeedFolder(_existingDir, enabled: true);
        IFileWatcher watcher = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);
        PendingFileQueue queue = new();

        FileWatcherWorker sut = NewWorker(factory, queue);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        await sut.StopAsync(CancellationToken.None);

        watcher.Received(1).Dispose();
        sut.RegisteredCount.Should().Be(0);
    }

    // ──────────────────────────────────────────────────────────────
    // 信号驱动的增量重建
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShareRecovered_Signal_Mounts_Watcher_And_Triggers_FolderScan()
    {
        // 启动时目录不可达（不存在）→ 注册被跳过；恢复后经信号补挂载并触发该目录补扫
        string shareDir = Path.Combine(_existingDir, "nas-share");
        long folderId = SeedFolder(shareDir, enabled: true);

        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ => NewStubWatcher());
        WatchRebuildSignal signal = new();
        IScanService scan = Substitute.For<IScanService>();
        scan.ScanFolderAsync(folderId, Arg.Any<CancellationToken>())
            .Returns(new ScanFolderResult("scan-1", folderId, shareDir, 0));

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue(), signal, scan);
        await sut.StartAsync(CancellationToken.None);
        await sut.StartupRegistrationCompleted; // 等首轮挂载结束，避免初始枚举与下面的建目录竞态
        sut.RegisteredCount.Should().Be(0, "启动时目录不存在应跳过注册");

        // 模拟共享恢复：目录重新可达 + NetworkShareMonitorWorker 发 ShareRecovered 信号
        Directory.CreateDirectory(shareDir);
        signal.Publish(new WatchRebuildItem(WatchChangeKind.ShareRecovered, folderId, shareDir));

        await WaitUntil(() => sut.RegisteredCount == 1);
        sut.RegisteredCount.Should().Be(1, "恢复信号应触发重新挂载 watcher");
        factory.Received(1).Create(shareDir);
        await WaitUntil(() => scan.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IScanService.ScanFolderAsync)));
        await scan.Received(1).ScanFolderAsync(folderId, Arg.Any<CancellationToken>());

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShareLost_Signal_Unmounts_Watcher()
    {
        long folderId = SeedFolder(_existingDir, enabled: true);
        IFileWatcher watcher = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue());
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.ShareLost, folderId, _existingDir), CancellationToken.None);

        sut.RegisteredCount.Should().Be(0, "共享不可达应立即卸载该目录 watcher");
        watcher.Received(1).Dispose();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FolderCreated_Signal_Mounts_New_Folder_At_Runtime()
    {
        // 启动时无目录；运行期新增一条启用目录 + 发 FolderCreated 信号 → 即刻挂载
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ => NewStubWatcher());

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue());
        await sut.StartAsync(CancellationToken.None);
        await sut.StartupRegistrationCompleted; // 等首轮挂载结束，避免初始枚举把下面新种的目录抢先注册
        sut.RegisteredCount.Should().Be(0);

        string newDir = CreateSubDir("added-at-runtime");
        long folderId = SeedFolder(newDir, enabled: true);

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.FolderCreated, folderId, newDir), CancellationToken.None);

        sut.RegisteredCount.Should().Be(1, "运行期新增目录应即刻挂载，不必重启进程");
        factory.Received(1).Create(newDir);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FolderUpdated_Signal_Disabled_Unmounts_Watcher()
    {
        long folderId = SeedFolder(_existingDir, enabled: true);
        IFileWatcher watcher = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue());
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        // 运行期禁用该目录
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            WatchFolder folder = ctx.WatchFolders.Single(f => f.Id == folderId);
            folder.Enabled = false;
            ctx.SaveChanges();
        }

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.FolderUpdated, folderId, _existingDir), CancellationToken.None);

        sut.RegisteredCount.Should().Be(0, "禁用的目录应立即停掉 watcher，不再继续采集");
        watcher.Received(1).Dispose();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FolderUpdated_Signal_AutoScanDisabled_Unmounts_Watcher()
    {
        long folderId = SeedFolder(_existingDir, enabled: true); // AutoScan 默认 true → 已挂载
        IFileWatcher watcher = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue());
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        // 运行期关闭该目录的自动监控
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            WatchFolder folder = ctx.WatchFolders.Single(f => f.Id == folderId);
            folder.AutoScan = false;
            ctx.SaveChanges();
        }

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.FolderUpdated, folderId, _existingDir), CancellationToken.None);

        sut.RegisteredCount.Should().Be(0, "运行期关闭自动监控应停掉 watcher，仅保留手动扫描");
        watcher.Received(1).Dispose();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FolderDeleted_Signal_Unmounts_Watcher()
    {
        long folderId = SeedFolder(_existingDir, enabled: true);
        IFileWatcher watcher = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(watcher);

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue());
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.FolderDeleted, folderId, _existingDir), CancellationToken.None);

        sut.RegisteredCount.Should().Be(0, "已删除的目录应立即卸载 watcher");
        watcher.Received(1).Dispose();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WatcherFaulted_Signal_Rebuilds_By_Path_And_Rescans()
    {
        // FSW Error（缓冲溢出 / 句柄失效）→ WatcherAdapter 只知道 Path，worker 按已注册 watcher 反查 FolderId 重建 + 补扫
        long folderId = SeedFolder(_existingDir, enabled: true);
        IFileWatcher first = NewStubWatcher();
        IFileWatcher second = NewStubWatcher();
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(first, second);
        IScanService scan = Substitute.For<IScanService>();
        scan.ScanFolderAsync(folderId, Arg.Any<CancellationToken>())
            .Returns(new ScanFolderResult("scan-2", folderId, _existingDir, 0));

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue(), scan: scan);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.WatcherFaulted, FolderId: 0, Path: _existingDir), CancellationToken.None);

        sut.RegisteredCount.Should().Be(1, "故障重建后应仍有 1 个 watcher（新实例）");
        first.Received(1).Dispose();
        second.Received(1).Start();
        factory.Received(2).Create(_existingDir);
        await scan.Received(1).ScanFolderAsync(folderId, Arg.Any<CancellationToken>());

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FolderScan_Busy_Retries_Then_Succeeds()
    {
        // 补扫撞「已有扫描在进行中」→ 按重试间隔重试后成功
        string shareDir = CreateSubDir("retry-share");
        long folderId = SeedFolder(shareDir, enabled: true);
        IFileWatcherFactory factory = Substitute.For<IFileWatcherFactory>();
        factory.Create(Arg.Any<string>()).Returns(_ => NewStubWatcher());
        IScanService scan = Substitute.For<IScanService>();
        int calls = 0;
        scan.ScanFolderAsync(folderId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new BusinessException("已有扫描在进行中");
                return new ScanFolderResult("scan-3", folderId, shareDir, 2);
            });

        FileWatcherWorker sut = NewWorker(factory, new PendingFileQueue(), scan: scan);
        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => sut.RegisteredCount == 1);

        await sut.HandleRebuildSignalAsync(
            new WatchRebuildItem(WatchChangeKind.ShareRecovered, folderId, shareDir), CancellationToken.None);

        calls.Should().Be(2, "首次撞扫描锁后应重试并成功");

        await sut.StopAsync(CancellationToken.None);
    }

    // ──────────────────────────────────────────────────────────────
    // 帮助方法
    // ──────────────────────────────────────────────────────────────

    private FileWatcherWorker NewWorker(
        IFileWatcherFactory factory,
        IPendingFileQueue queue,
        IWatchRebuildSignal? signal = null,
        IScanService? scan = null)
    {
        // FileWatcherWorker 现依赖 IFileIntakeService（不再直接持有队列）；用替身把 AdmitAsync 转成
        // 「入队到测试队列」，保持原有「事件→入队」断言不变。建 Detected 行 + SourcePath 幂等的职责
        // 由 Persistence 层（ScanServiceTests / FileIntakeService）覆盖，此处只验证 worker 的事件转发。
        IFileIntakeService intake = Substitute.For<IFileIntakeService>();
        intake.AdmitAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<PendingFileSource>(), Arg.Any<CancellationToken>())
            .Returns(ci => EnqueueAndReturn(queue, ci));
        return new(
            _dbFactory, factory, intake,
            signal ?? new WatchRebuildSignal(),
            BuildScopeFactory(scan),
            NullLogger<FileWatcherWorker>.Instance,
            scanRetryDelay: TimeSpan.FromMilliseconds(30));
    }

    /// <summary>构造仅含 Scoped IScanService 的作用域工厂（worker 补扫时经作用域解析）</summary>
    private static IServiceScopeFactory BuildScopeFactory(IScanService? scan)
    {
        IScanService stub = scan ?? Substitute.For<IScanService>();
        ServiceCollection services = new();
        services.AddScoped<IScanService>(_ => stub);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>替身委托：把 AdmitAsync 调用原样入队到测试队列并返回 true（模拟「新文件已登记入队」）</summary>
    private static async Task<bool> EnqueueAndReturn(IPendingFileQueue queue, NSubstitute.Core.CallInfo ci)
    {
        await queue.EnqueueAsync(
            new PendingFileItem(ci.ArgAt<string>(0), ci.ArgAt<long>(1), ci.ArgAt<PendingFileSource>(2)),
            ci.ArgAt<CancellationToken>(3));
        return true;
    }

    private long SeedFolder(string path, bool enabled, int priority = 100)
        => SeedFolders((path, enabled, priority))[0];

    private string CreateSubDir(string name)
    {
        string p = Path.Combine(_existingDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private long[] SeedFolders(params (string path, bool enabled, int priority)[] folders)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        long[] ids = new long[folders.Length];
        for (int i = 0; i < folders.Length; i++)
        {
            WatchFolder wf = new()
            {
                Path = folders[i].path,
                Enabled = folders[i].enabled,
                Priority = folders[i].priority,
            };
            ctx.WatchFolders.Add(wf);
            ctx.SaveChanges();
            ids[i] = wf.Id;
        }
        return ids;
    }

    private static IFileWatcher NewStubWatcher()
    {
        IFileWatcher w = Substitute.For<IFileWatcher>();
        return w;
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 1500)
    {
        int waited = 0;
        while (!predicate() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }

    private static async Task<PendingFileItem> ReadOne(PendingFileQueue queue)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        return await queue.Reader.ReadAsync(cts.Token);
    }

    /// <summary>测试 IDbContextFactory：每次 CreateDbContext 返回共享 SqliteConnection 的新上下文</summary>
    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection connection) { _connection = connection; }

        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }

    /// <summary>合成 watcher：Dispose / Start / Stop 走空，Fire 触发订阅者回调</summary>
    private sealed class SyntheticWatcher : IFileWatcher
    {
        public event Action<FileWatcherEvent>? OnFileEvent;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Fire(FileWatcherEvent evt) => OnFileEvent?.Invoke(evt);
    }
}
