using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>WatcherAdapter（D4.7）— FileSystemWatcher 包装的事件回调 + 生命周期</summary>
/// <remarks>
/// 真实 FileSystemWatcher 在 CI / 本地都能跑（不依赖管理员权限）；
/// 异步等待事件触发用 SemaphoreSlim + 1.5s 超时（FSW 实测延迟通常 &lt; 200ms）。
/// </remarks>
public sealed class WatcherAdapterTests : IDisposable
{
    private readonly string _workDir;

    public WatcherAdapterTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"pmm-watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Create_Then_FileCreated_RaisesCreatedEvent()
    {
        using WatcherAdapter w = new(_workDir, NullLogger<WatcherAdapter>.Instance);
        List<FileWatcherEvent> events = new();
        SemaphoreSlim sem = new(0, 1);

        w.OnFileEvent += e =>
        {
            if (e.ChangeType == FileWatcherChangeType.Created)
            {
                events.Add(e);
                sem.Release();
            }
        };
        w.Start();

        string newFile = Path.Combine(_workDir, "x.mkv");
        await File.WriteAllTextAsync(newFile, "hi");

        bool fired = await sem.WaitAsync(TimeSpan.FromSeconds(3));
        fired.Should().BeTrue("Created 事件应在 3s 内触发");
        events.Should().Contain(e => e.FullPath == newFile);
    }

    [Fact]
    public async Task Rename_RaisesRenamedEvent_WithOldPath()
    {
        string old = Path.Combine(_workDir, "old.mkv");
        await File.WriteAllTextAsync(old, "x");

        using WatcherAdapter w = new(_workDir, NullLogger<WatcherAdapter>.Instance);
        SemaphoreSlim sem = new(0, 1);
        FileWatcherEvent? renameEv = null;

        w.OnFileEvent += e =>
        {
            if (e.ChangeType == FileWatcherChangeType.Renamed)
            {
                renameEv = e;
                sem.Release();
            }
        };
        w.Start();

        string newPath = Path.Combine(_workDir, "new.mkv");
        File.Move(old, newPath);

        (await sem.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue();
        renameEv!.FullPath.Should().Be(newPath);
        renameEv.OldFullPath.Should().Be(old);
    }

    [Fact]
    public async Task SubdirectoryFile_RaisesEvent_DueToRecursiveWatch()
    {
        string sub = Path.Combine(_workDir, "subdir");
        Directory.CreateDirectory(sub);

        using WatcherAdapter w = new(_workDir, NullLogger<WatcherAdapter>.Instance);
        SemaphoreSlim sem = new(0, 1);
        w.OnFileEvent += e =>
        {
            if (e.ChangeType == FileWatcherChangeType.Created && e.FullPath.EndsWith("nested.mkv"))
                sem.Release();
        };
        w.Start();

        await File.WriteAllTextAsync(Path.Combine(sub, "nested.mkv"), "x");
        (await sem.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue("IncludeSubdirectories=true");
    }

    [Fact]
    public void NonexistentPath_ThrowsDirectoryNotFound()
    {
        Action a = () => new WatcherAdapter("/nope/nope/nope/nope", NullLogger<WatcherAdapter>.Instance);
        a.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Stop_StopsFiringEvents()
    {
        using WatcherAdapter w = new(_workDir, NullLogger<WatcherAdapter>.Instance);
        int callbacks = 0;
        w.OnFileEvent += _ => Interlocked.Increment(ref callbacks);
        w.Start();
        w.Stop();

        await File.WriteAllTextAsync(Path.Combine(_workDir, "y.mkv"), "x");
        await Task.Delay(500);   // 给足够时间让事件触发（如果还在监听）
        callbacks.Should().Be(0);
    }

    [Fact]
    public void Dispose_TwiceSafe()
    {
        WatcherAdapter w = new(_workDir, NullLogger<WatcherAdapter>.Instance);
        w.Dispose();
        Action a = () => w.Dispose();
        a.Should().NotThrow();
    }

    [Fact]
    public async Task Factory_CreatesIndependentWatchers()
    {
        WatcherAdapterFactory f = new(NullLoggerFactory.Instance);
        using IFileWatcher w1 = f.Create(_workDir);
        string second = Path.Combine(_workDir, "branch");
        Directory.CreateDirectory(second);
        using IFileWatcher w2 = f.Create(second);
        ReferenceEquals(w1, w2).Should().BeFalse("每次 Create 应得新实例");
        await Task.CompletedTask;
    }
}
