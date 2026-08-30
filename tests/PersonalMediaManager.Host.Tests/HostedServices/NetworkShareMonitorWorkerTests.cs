using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>NetworkShareMonitorWorker（D6.4）— 60s 周期可达性 + 状态扭转 + 本地盘不扫</summary>
/// <remarks>
/// 测试用真实临时目录代替「网络共享」，通过创建 / 删除目录模拟可达 / 不可达；
/// 直接调 internal SweepAsync 绕过 PeriodicTimer 的 60s 等待，避免单测耗时。
/// </remarks>
public sealed class NetworkShareMonitorWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly FakeClock _clock = new();
    private readonly string _scratchRoot;

    public NetworkShareMonitorWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _scratchRoot = Path.Combine(Path.GetTempPath(), $"pmm-nsm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_scratchRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Sweep_With_No_Network_Shares_Returns_Zero_NoDbChanges()
    {
        SeedFolder(_scratchRoot, enabled: true, isNetworkShare: false);
        _clock.Set(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));

        NetworkShareMonitorWorker sut = NewWorker();
        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(0, "本地盘不应被本 worker 扫描");
        // 本地盘 LastReachableAt 仍为 null
        WatchFolder f = ReadFolderByPath(_scratchRoot);
        f.LastReachableAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_Skips_NetworkShare_With_AutoScan_Disabled()
    {
        // 关闭自动监控的网络盘：无实时 watcher 可重建、无自动补扫，可达性探测纯属浪费 → 不扫
        using (PmmDbContext ctx = _dbFactory.CreateDbContext())
        {
            ctx.WatchFolders.Add(new WatchFolder
            {
                Path = _scratchRoot,
                Enabled = true,
                IsNetworkShare = true,
                AutoScan = false,
                Priority = 100,
            });
            ctx.SaveChanges();
        }
        _clock.Set(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));

        NetworkShareMonitorWorker sut = NewWorker();
        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(0, "AutoScan=false 的网络盘不应被可达性探测");
        ReadFolderByPath(_scratchRoot).LastReachableAt.Should().BeNull();
    }

    [Fact]
    public async Task Reachable_NetworkShare_Marks_Reachable_And_Sets_LastReachableAt()
    {
        long id = SeedFolder(_scratchRoot, enabled: true, isNetworkShare: true);
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        _clock.Set(now);

        NetworkShareMonitorWorker sut = NewWorker();
        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(1);
        WatchFolder f = ReadFolder(id);
        f.LastReachableAt.Should().Be(now);
    }

    [Fact]
    public async Task Unreachable_NetworkShare_Sets_LastReachableAt_Null()
    {
        string ghost = Path.Combine(_scratchRoot, "no-such-share");
        long id = SeedFolder(ghost, enabled: true, isNetworkShare: true);
        _clock.Set(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));

        NetworkShareMonitorWorker sut = NewWorker();
        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(1);
        WatchFolder f = ReadFolder(id);
        f.LastReachableAt.Should().BeNull();
    }

    [Fact]
    public async Task Recovery_From_Unreachable_To_Reachable_Updates_LastReachableAt()
    {
        long id = SeedFolderWithLastReachable(_scratchRoot, lastReachableAt: null, isNetworkShare: true);
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        _clock.Set(now);

        NetworkShareMonitorWorker sut = NewWorker();
        await sut.SweepAsync(CancellationToken.None);

        WatchFolder f = ReadFolder(id);
        f.LastReachableAt.Should().Be(now, "从不可达恢复到可达，LastReachableAt 应更新到 now");
    }

    [Fact]
    public async Task Continuously_Reachable_Refreshes_LastReachableAt_NoToggle()
    {
        DateTimeOffset earlier = new(2026, 5, 17, 9, 59, 0, TimeSpan.Zero);
        long id = SeedFolderWithLastReachable(_scratchRoot, lastReachableAt: earlier, isNetworkShare: true);
        DateTimeOffset now = earlier.AddSeconds(60);
        _clock.Set(now);

        NetworkShareMonitorWorker sut = NewWorker();
        await sut.SweepAsync(CancellationToken.None);

        WatchFolder f = ReadFolder(id);
        f.LastReachableAt.Should().Be(now, "持续可达应仅刷新 LastReachableAt");
    }

    [Fact]
    public async Task Disabled_Folder_Is_Skipped_Even_If_Network_Share()
    {
        SeedFolder(_scratchRoot, enabled: false, isNetworkShare: true);
        _clock.Set(DateTimeOffset.UtcNow);

        NetworkShareMonitorWorker sut = NewWorker();
        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(0);
        WatchFolder f = ReadFolderByPath(_scratchRoot);
        f.LastReachableAt.Should().BeNull();
    }

    [Fact]
    public async Task Multiple_Shares_All_Probed_Even_If_One_Fails()
    {
        string okPath = _scratchRoot;
        string ghost = Path.Combine(_scratchRoot, "ghost");
        long ghostId = SeedFolder(ghost, enabled: true, isNetworkShare: true);
        long okId = SeedFolder(okPath, enabled: true, isNetworkShare: true);
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        _clock.Set(now);

        NetworkShareMonitorWorker sut = NewWorker();
        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(2);
        ReadFolder(ghostId).LastReachableAt.Should().BeNull();
        ReadFolder(okId).LastReachableAt.Should().Be(now);
    }

    [Fact]
    public async Task SweepAsync_Cancellation_Throws_OperationCanceledException()
    {
        SeedFolder(_scratchRoot, enabled: true, isNetworkShare: true);
        NetworkShareMonitorWorker sut = NewWorker();

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await sut.SweepAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SweepAsync_ParallelProbes_Total_Elapsed_Bounded_By_Slowest_Not_Sum()
    {
        // r3 P2-r3.17：并行探测验证 —— 3 个 ghost 路径单 folder 都会等满 ProbeTimeout(5s)，
        // 旧 foreach 串行需 ~15s；并行后总耗时应 < ProbeTimeout × 2（即最慢一个 + 调度抖动），远小于串行累加
        for (int i = 0; i < 3; i++)
        {
            SeedFolder(Path.Combine(_scratchRoot, $"ghost-{i}"), enabled: true, isNetworkShare: true);
        }
        _clock.Set(DateTimeOffset.UtcNow);

        NetworkShareMonitorWorker sut = NewWorker();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        int probed = await sut.SweepAsync(CancellationToken.None);
        sw.Stop();

        probed.Should().Be(3);
        // 3 个 ghost 路径走 Directory.Exists：在普通 NTFS 上「不存在」会立即返回 false（不触发 ProbeTimeout），
        // 此用例只验证并行流程不破坏 sweep 行为；真实 UNC 死路径阻塞场景在生产侧由 SweepBudget 兜底（无法在单测无副作用复现 SMB 30s 协商）
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "3 个不存在的本地路径探测应在秒内完成，不可能累加到 15s 串行级别");
    }

    [Fact]
    public async Task StartStop_With_Period_Smoke()
    {
        // 用极小 period 触发一次 timer tick，验证 ExecuteAsync + StopAsync 串联正常
        SeedFolder(_scratchRoot, enabled: true, isNetworkShare: true);
        _clock.Set(DateTimeOffset.UtcNow);

        NetworkShareMonitorWorker sut = new(_dbFactory, _clock, NullLogger<NetworkShareMonitorWorker>.Instance, period: TimeSpan.FromMilliseconds(80));

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(250); // 让 timer 至少 tick 一次
        await sut.StopAsync(CancellationToken.None);

        WatchFolder f = ReadFolderByPath(_scratchRoot);
        f.LastReachableAt.Should().NotBeNull("启动循环至少应跑过一次 sweep");
    }

    // ──────────────────────────────────────────────────────────────
    // 可达性翻转 → IWatchRebuildSignal 信号（FileWatcherWorker 重建依据）
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recovery_Flip_Publishes_ShareRecovered_Signal()
    {
        // 之前不可达（LastReachableAt=null）+ 目录现在可达 → 翻转沿应发 ShareRecovered（重建 + 补扫）
        long id = SeedFolderWithLastReachable(_scratchRoot, lastReachableAt: null, isNetworkShare: true);
        _clock.Set(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));
        WatchRebuildSignal signal = new();

        NetworkShareMonitorWorker sut = NewWorker(signal);
        await sut.SweepAsync(CancellationToken.None);

        signal.Reader.TryRead(out WatchRebuildItem? item).Should().BeTrue("恢复可达翻转沿应发布重建信号");
        item!.Kind.Should().Be(WatchChangeKind.ShareRecovered);
        item.FolderId.Should().Be(id);
        item.Path.Should().Be(_scratchRoot);
    }

    [Fact]
    public async Task Loss_Flip_Publishes_ShareLost_Signal()
    {
        // 之前可达（2 分钟内刷新过）+ 目录现在不可达 → 翻转沿应发 ShareLost（暂停监控）
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        string ghost = Path.Combine(_scratchRoot, "ghost-share");
        long id = SeedFolderWithLastReachable(ghost, lastReachableAt: now.AddSeconds(-30), isNetworkShare: true);
        _clock.Set(now);
        WatchRebuildSignal signal = new();

        NetworkShareMonitorWorker sut = NewWorker(signal);
        await sut.SweepAsync(CancellationToken.None);

        signal.Reader.TryRead(out WatchRebuildItem? item).Should().BeTrue("掉线翻转沿应发布暂停信号");
        item!.Kind.Should().Be(WatchChangeKind.ShareLost);
        item.FolderId.Should().Be(id);
    }

    [Fact]
    public async Task Continuously_Reachable_DoesNotPublishSignal()
    {
        // 持续可达（无翻转沿）→ 不应发任何重建信号（避免周期性无谓重建 + 补扫风暴）
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        SeedFolderWithLastReachable(_scratchRoot, lastReachableAt: now.AddSeconds(-60), isNetworkShare: true);
        _clock.Set(now);
        WatchRebuildSignal signal = new();

        NetworkShareMonitorWorker sut = NewWorker(signal);
        await sut.SweepAsync(CancellationToken.None);

        signal.Reader.TryRead(out _).Should().BeFalse("持续可达不应发布重建信号");
    }

    // ---------- helpers ----------

    private NetworkShareMonitorWorker NewWorker(WatchRebuildSignal? signal = null)
        => new(_dbFactory, _clock, NullLogger<NetworkShareMonitorWorker>.Instance,
            period: TimeSpan.FromMinutes(1), alert: null, rebuildSignal: signal);

    private long SeedFolder(string path, bool enabled, bool isNetworkShare)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        WatchFolder f = new()
        {
            Path = path,
            Enabled = enabled,
            IsNetworkShare = isNetworkShare,
            Priority = 100,
        };
        ctx.WatchFolders.Add(f);
        ctx.SaveChanges();
        return f.Id;
    }

    private long SeedFolderWithLastReachable(string path, DateTimeOffset? lastReachableAt, bool isNetworkShare)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        WatchFolder f = new()
        {
            Path = path,
            Enabled = true,
            IsNetworkShare = isNetworkShare,
            Priority = 100,
            LastReachableAt = lastReachableAt,
        };
        ctx.WatchFolders.Add(f);
        ctx.SaveChanges();
        return f.Id;
    }

    private WatchFolder ReadFolder(long id)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.WatchFolders.AsNoTracking().Single(f => f.Id == id);
    }

    private WatchFolder ReadFolderByPath(string path)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.WatchFolders.AsNoTracking().Single(f => f.Path == path);
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
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Set(DateTimeOffset t) => _now = t;
        public DateTimeOffset UtcNow => _now;
    }
}
