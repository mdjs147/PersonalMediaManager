using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;
using PersonalMediaManager.Infrastructure.Persistence.Services.Scan;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>D7.6 ScanService — 全量 / 单目录 / 去重 / 并发锁 / IFullScanCoordinator 复用</summary>
public sealed class ScanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly InMemoryQueue _queue = new();
    private readonly FileIntakeService _intake;
    private readonly ScanService _sut;
    private readonly string _scratch;

    public ScanServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _scratch = Path.Combine(Path.GetTempPath(), $"pmm-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);

        _intake = new FileIntakeService(_dbFactory, _queue, new FakeMediaExtensionProvider(), NullLogger<FileIntakeService>.Instance);
        _sut = new ScanService(_dbFactory, _queue, _intake, new FakeMediaExtensionProvider(), NullLogger<ScanService>.Instance);
        ScanService.ResetInFlightForTests();
    }

    public void Dispose()
    {
        ScanService.ResetInFlightForTests();
        _connection.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ---------- 单目录 ----------

    [Fact]
    public async Task ScanFolder_HappyPath_Enqueues_All_Videos_Recursively()
    {
        string folderPath = Path.Combine(_scratch, "downloads");
        Directory.CreateDirectory(folderPath);
        string sub = Path.Combine(folderPath, "sub");
        Directory.CreateDirectory(sub);
        string v1 = WriteFile(Path.Combine(folderPath, "a.mkv"));
        string v2 = WriteFile(Path.Combine(folderPath, "b.mp4"));
        string v3 = WriteFile(Path.Combine(sub, "c.mkv"));
        WriteFile(Path.Combine(folderPath, "ignore.txt")); // 非视频后缀跳过
        long fId = SeedFolder(folderPath, enabled: true);

        ScanFolderResult r = await _sut.ScanFolderAsync(fId);

        r.FilesEnqueued.Should().Be(3);
        _queue.Items.Select(i => i.FullPath).Should().BeEquivalentTo(new[] { v1, v2, v3 });
        _queue.Items.Should().AllSatisfy(i => i.Source.Should().Be(PendingFileSource.Manual));
        _queue.Items.Should().AllSatisfy(i => i.WatchFolderId.Should().Be(fId));
    }

    [Fact]
    public async Task ScanFolder_Skips_Files_With_Existing_MediaItem()
    {
        string folderPath = Path.Combine(_scratch, "x");
        Directory.CreateDirectory(folderPath);
        string newFile = WriteFile(Path.Combine(folderPath, "new.mkv"));
        string existingFile = WriteFile(Path.Combine(folderPath, "existing.mkv"));
        SeedMediaItem(existingFile);
        long fId = SeedFolder(folderPath, enabled: true);

        ScanFolderResult r = await _sut.ScanFolderAsync(fId);

        r.FilesEnqueued.Should().Be(1);
        _queue.Items.Should().ContainSingle().Which.FullPath.Should().Be(newFile);
    }

    [Fact]
    public async Task ScanFolder_Missing_Folder_Throws()
    {
        Func<Task> act = async () => await _sut.ScanFolderAsync(99999);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("监控目录不存在");
    }

    [Fact]
    public async Task ScanFolder_Disabled_Throws()
    {
        long fId = SeedFolder(_scratch, enabled: false);
        Func<Task> act = async () => await _sut.ScanFolderAsync(fId);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("目录已禁用");
    }

    [Fact]
    public async Task ScanFolder_Unreachable_Path_Throws()
    {
        string ghost = Path.Combine(_scratch, "no-such");
        long fId = SeedFolder(ghost, enabled: true);
        Func<Task> act = async () => await _sut.ScanFolderAsync(fId);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("目录不可达*");
    }

    [Fact(DisplayName = "健壮枚举：不可访问子目录被跳过，其余文件仍正常入队（不中断整次扫描）")]
    public async Task ScanFolder_Inaccessible_Subdirectory_Still_Enqueues_Other_Files()
    {
        // 复现并守护旧 SearchOption.AllDirectories 缺陷：惰性递归到不可读子目录时会在 foreach 内抛
        // UnauthorizedAccessException 逃逸、令整次扫描中断、后续可访问文件被静默丢弃。
        // 新实现走 EnumerationOptions{IgnoreInaccessible=true} + MoveNext 防御 → 坏子目录跳过、其余照常入队。
        string folderPath = Path.Combine(_scratch, "downloads");
        Directory.CreateDirectory(folderPath);

        // 可访问视频：根目录 1 个 + 正常子目录 1 个
        string root = WriteFile(Path.Combine(folderPath, "root.mkv"));
        string okSub = Path.Combine(folderPath, "ok");
        Directory.CreateDirectory(okSub);
        string nested = WriteFile(Path.Combine(okSub, "nested.mp4"));

        // 不可访问子目录：内含 1 个视频，随后剥夺「列举内容」权限
        string lockedSub = Path.Combine(folderPath, "locked");
        Directory.CreateDirectory(lockedSub);
        string hidden = WriteFile(Path.Combine(lockedSub, "hidden.mkv"));
        bool denied = TryMakeDirectoryUnlistable(lockedSub);

        long fId = SeedFolder(folderPath, enabled: true);

        try
        {
            // 关键断言：不可访问子目录不得让整次扫描抛错（旧实现此处会抛 UnauthorizedAccessException）
            await _sut.ScanFolderAsync(fId);

            List<string> enqueued = _queue.Items.Select(i => i.FullPath).ToList();
            enqueued.Should().Contain(new[] { root, nested },
                "不可访问子目录应被跳过，根目录与正常子目录的可访问视频仍全部入队，而非整次扫描中断");

            if (denied)
            {
                enqueued.Should().NotContain(hidden,
                    "已确认权限剥夺生效：不可访问子目录内的文件应被 IgnoreInaccessible 跳过");
            }
        }
        finally
        {
            RestoreDirectoryAccess(lockedSub, denied);
        }
    }

    // ---------- 全量 ----------

    [Fact]
    public async Task TriggerAll_Empty_Throws()
    {
        Func<Task> act = async () => await _sut.TriggerAllAsync();
        await act.Should().ThrowAsync<BusinessException>().WithMessage("未配置任何监控目录");
    }

    [Fact(DisplayName = "B1：RunAsync 零启用目录场景 noop 不抛错（区别于 TriggerAllAsync 手动触发）")]
    public async Task RunAsync_Empty_Folders_Noop_Does_Not_Throw()
    {
        // B1（F.4 冒烟发现）：FullScanJob 周期触发零配置场景不视为错误
        // 手动 TriggerAllAsync 走「先配置监控目录」抛 BusinessException 引导用户；
        // Quartz Job 走 RunAsync 直接 noop，让 ScheduledTaskRunRecorder 落 Succeeded + Processed=0
        // 入口锁不变（与 TriggerAllAsync 共享 _inFlight），后续手动触发不受影响
        IFullScanCoordinator coordinator = _sut; // 显式按 IFullScanCoordinator 契约调用，避免误读成 IScanService 路径

        Func<Task> act = async () => await coordinator.RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("零监控目录是首启正常状态，不应让 Recorder 落 Failed");
        _queue.Items.Should().BeEmpty("零目录不应入队任何文件");
    }

    [Fact(DisplayName = "B1：RunAsync 有目录正常走主扫描流程（与 TriggerAllAsync 等价）")]
    public async Task RunAsync_With_Folders_Runs_Full_Scan_Like_TriggerAllAsync()
    {
        // 守护 B1 重构未误改非空路径：RunAsync 有目录时仍应入队文件，与 TriggerAllAsync 同语义
        string folderPath = Path.Combine(_scratch, "via-run");
        Directory.CreateDirectory(folderPath);
        WriteFile(Path.Combine(folderPath, "x.mkv"));
        WriteFile(Path.Combine(folderPath, "y.mp4"));
        SeedFolder(folderPath, enabled: true);

        IFullScanCoordinator coordinator = _sut;
        await coordinator.RunAsync(CancellationToken.None);

        _queue.Items.Should().HaveCount(2, "RunAsync 非空目录应与 TriggerAllAsync 走同一扫描主流程");
        _queue.Items.Should().AllSatisfy(i => i.Source.Should().Be(PendingFileSource.FullScan));
    }

    [Fact]
    public async Task TriggerAll_Enumerates_Enabled_Folders_Only()
    {
        string fa = Path.Combine(_scratch, "fa"); Directory.CreateDirectory(fa);
        string fb = Path.Combine(_scratch, "fb"); Directory.CreateDirectory(fb);
        string fc = Path.Combine(_scratch, "fc"); Directory.CreateDirectory(fc);
        WriteFile(Path.Combine(fa, "a.mkv"));
        WriteFile(Path.Combine(fb, "b.mkv"));
        WriteFile(Path.Combine(fc, "c.mkv")); // 禁用目录里的不算
        SeedFolder(fa, enabled: true);
        SeedFolder(fb, enabled: true);
        SeedFolder(fc, enabled: false);

        ScanTriggerResult r = await _sut.TriggerAllAsync();

        r.FolderCount.Should().Be(2, "FolderCount 只计入启用目录");
        r.FilesEnqueued.Should().Be(2);
        _queue.Items.Should().AllSatisfy(i => i.Source.Should().Be(PendingFileSource.FullScan));
    }

    [Fact]
    public async Task TriggerAll_Unreachable_Folder_Skipped_Not_Throw()
    {
        string okFolder = Path.Combine(_scratch, "ok");
        Directory.CreateDirectory(okFolder);
        WriteFile(Path.Combine(okFolder, "a.mkv"));
        string ghostFolder = Path.Combine(_scratch, "ghost");
        SeedFolder(ghostFolder, enabled: true);
        SeedFolder(okFolder, enabled: true);

        ScanTriggerResult r = await _sut.TriggerAllAsync();

        r.FilesEnqueued.Should().Be(1, "不可达目录跳过，可达目录继续");
    }

    // ---------- 自动监控开关 AutoScan ----------

    [Fact(DisplayName = "AutoScan：周期 RunAsync 跳过 AutoScan=false 目录（仅自动监控目录入周期扫描）")]
    public async Task RunAsync_Skips_AutoScan_Disabled_Folders()
    {
        string autoDir = Path.Combine(_scratch, "auto"); Directory.CreateDirectory(autoDir);
        string manualDir = Path.Combine(_scratch, "manual-only"); Directory.CreateDirectory(manualDir);
        WriteFile(Path.Combine(autoDir, "a.mkv"));
        WriteFile(Path.Combine(manualDir, "m.mkv")); // 仅手动目录的文件不应被周期任务入队
        SeedFolder(autoDir, enabled: true, autoScan: true);
        SeedFolder(manualDir, enabled: true, autoScan: false);

        await ((IFullScanCoordinator)_sut).RunAsync(CancellationToken.None);

        _queue.Items.Should().ContainSingle("周期扫描只覆盖 AutoScan=true 目录");
        _queue.Items[0].FullPath.Should().Be(Path.Combine(autoDir, "a.mkv"));
    }

    [Fact(DisplayName = "AutoScan：手动 TriggerAllAsync 仍覆盖 AutoScan=false 目录（手动是显式动作，不受开关限制）")]
    public async Task TriggerAll_Includes_AutoScan_Disabled_Folders()
    {
        string autoDir = Path.Combine(_scratch, "auto"); Directory.CreateDirectory(autoDir);
        string manualDir = Path.Combine(_scratch, "manual-only"); Directory.CreateDirectory(manualDir);
        WriteFile(Path.Combine(autoDir, "a.mkv"));
        WriteFile(Path.Combine(manualDir, "m.mkv"));
        SeedFolder(autoDir, enabled: true, autoScan: true);
        SeedFolder(manualDir, enabled: true, autoScan: false);

        ScanTriggerResult r = await _sut.TriggerAllAsync();

        r.FolderCount.Should().Be(2, "手动「立即扫描全部」覆盖所有启用目录，含 AutoScan=false");
        r.FilesEnqueued.Should().Be(2);
    }

    [Fact(DisplayName = "AutoScan：单目录手动扫描对 AutoScan=false 启用目录照常工作")]
    public async Task ScanFolder_AutoScan_Disabled_Still_Scans()
    {
        string manualDir = Path.Combine(_scratch, "manual-only"); Directory.CreateDirectory(manualDir);
        WriteFile(Path.Combine(manualDir, "m.mkv"));
        long fId = SeedFolder(manualDir, enabled: true, autoScan: false);

        ScanFolderResult r = await _sut.ScanFolderAsync(fId);

        r.FilesEnqueued.Should().Be(1, "关闭自动监控只停自动扫描，手动点击此目录扫描仍应处理");
    }

    // ---------- 并发锁 ----------

    [Fact]
    public async Task Second_Scan_While_First_InFlight_Throws_BusyError()
    {
        long fId = SeedFolder(_scratch, enabled: true);
        // 第一次 acquire 后不释放，模拟另一线程进入
        AcquireInflightLockForTest();
        try
        {
            Func<Task> act = async () => await _sut.ScanFolderAsync(fId);
            await act.Should().ThrowAsync<BusinessException>().WithMessage("已有扫描在进行中");

            Func<Task> act2 = async () => await _sut.TriggerAllAsync();
            await act2.Should().ThrowAsync<BusinessException>().WithMessage("已有扫描在进行中");
        }
        finally
        {
            ScanService.ResetInFlightForTests();
        }
    }

    [Fact]
    public async Task Lock_Released_After_Successful_Scan_Allows_Next()
    {
        long fId = SeedFolder(_scratch, enabled: true);
        WriteFile(Path.Combine(_scratch, "a.mkv"));

        await _sut.ScanFolderAsync(fId);
        // 立即再次触发不应报忙
        Func<Task> act = async () => await _sut.ScanFolderAsync(fId);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Lock_Released_After_Failed_Scan()
    {
        long fId = SeedFolder(Path.Combine(_scratch, "ghost"), enabled: true);
        Func<Task> firstFail = async () => await _sut.ScanFolderAsync(fId);
        await firstFail.Should().ThrowAsync<BusinessException>();
        // 锁应该已释放，第二次失败的不是「已有扫描在进行中」而是「不可达」
        Func<Task> secondFail = async () => await _sut.ScanFolderAsync(fId);
        await secondFail.Should().ThrowAsync<BusinessException>().WithMessage("目录不可达*");
    }

    // ---------- 手动整理 ScanPath ----------

    [Fact(DisplayName = "手动整理：单视频文件入队（Manual + WatchFolderId=0）")]
    public async Task ScanPath_SingleVideoFile_Enqueues_As_Manual()
    {
        string file = WriteFile(Path.Combine(_scratch, "盗梦空间.2010.1080p.mkv"));

        ScanPathResult r = await _sut.ScanPathAsync(file);

        r.IsDirectory.Should().BeFalse();
        r.FilesEnqueued.Should().Be(1);
        r.Skipped.Should().Be(0);
        _queue.Items.Should().ContainSingle();
        _queue.Items[0].FullPath.Should().Be(file);
        _queue.Items[0].Source.Should().Be(PendingFileSource.Manual);
        _queue.Items[0].WatchFolderId.Should().Be(0);
    }

    [Fact(DisplayName = "手动整理：目录递归入队所有视频，跳过非视频")]
    public async Task ScanPath_Directory_Enqueues_Videos_Recursively()
    {
        string dir = Path.Combine(_scratch, "manual");
        Directory.CreateDirectory(dir);
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        string v1 = WriteFile(Path.Combine(dir, "a.mkv"));
        string v2 = WriteFile(Path.Combine(sub, "b.mp4"));
        WriteFile(Path.Combine(dir, "readme.txt"));

        ScanPathResult r = await _sut.ScanPathAsync(dir);

        r.IsDirectory.Should().BeTrue();
        r.FilesEnqueued.Should().Be(2);
        _queue.Items.Select(i => i.FullPath).Should().BeEquivalentTo(new[] { v1, v2 });
        _queue.Items.Should().AllSatisfy(i => i.Source.Should().Be(PendingFileSource.Manual));
    }

    [Fact(DisplayName = "手动整理：已存在同路径记录跳过并计入 Skipped")]
    public async Task ScanPath_Skips_Existing_And_Counts()
    {
        string dir = Path.Combine(_scratch, "dup");
        Directory.CreateDirectory(dir);
        string fresh = WriteFile(Path.Combine(dir, "fresh.mkv"));
        string dup = WriteFile(Path.Combine(dir, "dup.mkv"));
        SeedMediaItem(dup);

        ScanPathResult r = await _sut.ScanPathAsync(dir);

        r.FilesEnqueued.Should().Be(1);
        r.Skipped.Should().Be(1);
        _queue.Items.Should().ContainSingle().Which.FullPath.Should().Be(fresh);
    }

    [Fact(DisplayName = "手动整理：路径不存在抛 BusinessException")]
    public async Task ScanPath_NonExistent_Throws()
    {
        string ghost = Path.Combine(_scratch, "no-such-file.mkv");
        Func<Task> act = async () => await _sut.ScanPathAsync(ghost);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("路径不存在");
    }

    [Fact(DisplayName = "手动整理：非视频文件抛 BusinessException")]
    public async Task ScanPath_NonVideoFile_Throws()
    {
        string txt = WriteFile(Path.Combine(_scratch, "note.txt"));
        Func<Task> act = async () => await _sut.ScanPathAsync(txt);
        await act.Should().ThrowAsync<BusinessException>().WithMessage("不是受支持的视频文件");
    }

    // 说明：原 ScanPath_Empty_Throws（"路径不能为空"）已迁为 DTO 反射单测
    // （PersonalMediaManager.Application.Tests/Validation/ScanRequestValidationTests），
    // 该校验改由 ScanPathRequest DataAnnotations 在模型绑定阶段承接，service 不再抛异常。

    [Fact(DisplayName = "手动整理：完成后释放全局锁，可立即再次触发")]
    public async Task ScanPath_Releases_Lock_After_Success()
    {
        string file = WriteFile(Path.Combine(_scratch, "x.mkv"));
        await _sut.ScanPathAsync(file);

        string file2 = WriteFile(Path.Combine(_scratch, "y.mkv"));
        Func<Task> act = async () => await _sut.ScanPathAsync(file2);
        await act.Should().NotThrowAsync();
    }

    // ---------- IFullScanCoordinator 桥接 ----------

    [Fact]
    public async Task FullScanCoordinator_RunAsync_Calls_TriggerAll()
    {
        string fa = Path.Combine(_scratch, "fa"); Directory.CreateDirectory(fa);
        WriteFile(Path.Combine(fa, "x.mkv"));
        SeedFolder(fa, enabled: true);

        await ((IFullScanCoordinator)_sut).RunAsync(CancellationToken.None);
        _queue.Items.Should().ContainSingle();
        _queue.Items[0].Source.Should().Be(PendingFileSource.FullScan);
    }

    // ---------- helpers ----------

    private static void AcquireInflightLockForTest()
    {
        // 复用反射调用 AcquireLock 不必要 — 通过测试钩子模拟「已被另线程占用」
        // 取巧：连续两次 ScanFolderAsync 模拟 — 第一次不释放（throw 在内部），第二次必失败
        // 但更直接：让 _inFlight 字段位手动加 1。这里走 ResetInFlightForTests 反向使用：
        typeof(ScanService)
            .GetField("_inFlight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, 1);
    }

    private string WriteFile(string path)
    {
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>剥夺目录「列举内容」权限以模拟不可读子目录；返回是否生效（提权运行 / 不支持时退化为 false）</summary>
    private static bool TryMakeDirectoryUnlistable(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo di = new(dir);
                DirectorySecurity sec = di.GetAccessControl();
                SecurityIdentifier? me = WindowsIdentity.GetCurrent().User;
                if (me is null) return false;
                sec.AddAccessRule(new FileSystemAccessRule(
                    me,
                    FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Deny));
                di.SetAccessControl(sec);
                return true;
            }

            // Unix 兜底（产品仅支持 Windows）：清空权限位 → 无 r-x，无法列举子目录内容
            File.SetUnixFileMode(dir, UnixFileMode.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>恢复目录访问权限，确保 _scratch 能被递归删除（仅当先前确实剥夺成功才处理）</summary>
    private static void RestoreDirectoryAccess(string dir, bool wasDenied)
    {
        if (!wasDenied) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo di = new(dir);
                DirectorySecurity sec = di.GetAccessControl();
                SecurityIdentifier? me = WindowsIdentity.GetCurrent().User;
                if (me is not null) sec.PurgeAccessRules(me);
                di.SetAccessControl(sec);
            }
            else
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch
        {
            // 清理尽力而为：Dispose 里的 Directory.Delete 还有 try/catch 兜底
        }
    }

    private long SeedFolder(string path, bool enabled, bool autoScan = true)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        WatchFolder f = new() { Path = path, Enabled = enabled, AutoScan = autoScan, Priority = 100 };
        db.WatchFolders.Add(f);
        db.SaveChanges();
        return f.Id;
    }

    private void SeedMediaItem(string sourcePath)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateDetected(sourcePath, Path.GetFileName(sourcePath), 1);
        db.MediaItems.Add(item);
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
