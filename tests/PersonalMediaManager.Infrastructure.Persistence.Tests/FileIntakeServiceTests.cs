using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>FileIntakeService — 新发现文件「先落 Detected 行再入队」+ SourcePath 幂等</summary>
/// <remarks>
/// 守护本次修复核心：排队任务必须落库（Detected），否则「处理队列」页查不到积压、进程重启即丢队列。
/// 用真实 SQLite in-memory 验证建行 + 幂等去重；队列用记录式替身断言入队效果。
/// </remarks>
public sealed class FileIntakeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly RecordingQueue _queue = new();
    private readonly FileIntakeService _sut;

    public FileIntakeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _sut = new FileIntakeService(_dbFactory, _queue, new FakeMediaExtensionProvider(), NullLogger<FileIntakeService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "新文件：建 Detected 行 + 入队 + 返回 true")]
    public async Task Admit_NewFile_CreatesDetectedRow_AndEnqueues()
    {
        const string path = @"F:\Downloads\盗梦空间.2010.mkv";

        bool admitted = await _sut.AdmitAsync(path, watchFolderId: 7, PendingFileSource.Watcher);

        admitted.Should().BeTrue();
        _queue.Items.Should().ContainSingle();
        _queue.Items[0].FullPath.Should().Be(path);
        _queue.Items[0].WatchFolderId.Should().Be(7);
        _queue.Items[0].Source.Should().Be(PendingFileSource.Watcher);

        await using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem row = await db.MediaItems.SingleAsync(m => m.SourcePath == path);
        row.Status.Should().Be(MediaItemStatus.Detected, "排队任务必须以在途状态落库，处理队列页才查得到");
    }

    [Fact(DisplayName = "已存在同路径：不重复建行、不入队、返回 false")]
    public async Task Admit_ExistingPath_SkipsWithoutEnqueue()
    {
        const string path = @"F:\Downloads\星际穿越.2014.mkv";
        await _sut.AdmitAsync(path, 1, PendingFileSource.Manual);   // 首次登记
        _queue.Items.Clear();

        bool admitted = await _sut.AdmitAsync(path, 1, PendingFileSource.Manual);   // 二次同路径

        admitted.Should().BeFalse();
        _queue.Items.Should().BeEmpty("已存在同 SourcePath 不应重复入队");

        await using PmmDbContext db = _dbFactory.CreateDbContext();
        (await db.MediaItems.CountAsync(m => m.SourcePath == path)).Should().Be(1, "幂等：不应建出第二行");
    }

    [Theory(DisplayName = "非视频扩展名（含多重后缀临时文件）：拒收、不建行、返回 false")]
    [InlineData(@"F:\Downloads\庆余年.S01E01.mp4.bt.xltd")]   // 迅雷下载中：末段 .xltd 非视频
    [InlineData(@"F:\Downloads\盗梦空间.2010.mkv.part")]        // 命中白名单外 + 忽略种子 .part
    [InlineData(@"F:\Downloads\说明.txt")]
    public async Task Admit_NonVideoExtension_Rejected(string path)
    {
        bool admitted = await _sut.AdmitAsync(path, watchFolderId: 1, PendingFileSource.Watcher);

        admitted.Should().BeFalse();
        _queue.Items.Should().BeEmpty("非视频文件不应入队");

        await using PmmDbContext db = _dbFactory.CreateDbContext();
        (await db.MediaItems.CountAsync(m => m.SourcePath == path)).Should().Be(0, "非视频文件不应建 Detected 行");
    }

    [Fact(DisplayName = "命中关键词忽略规则的视频文件：拒收、不建行、返回 false")]
    public async Task Admit_KeywordIgnoreRuleHit_Rejected()
    {
        await using (PmmDbContext seed = _dbFactory.CreateDbContext())
        {
            seed.WatchIgnoreRules.Add(new WatchIgnoreRule
            {
                Type = IgnoreRuleType.Keyword,
                Pattern = "sample",
                Enabled = true,
            });
            await seed.SaveChangesAsync();
        }
        const string path = @"F:\Downloads\Inception.2010.Sample.mkv";

        bool admitted = await _sut.AdmitAsync(path, 1, PendingFileSource.Watcher);

        admitted.Should().BeFalse();
        _queue.Items.Should().BeEmpty();

        await using PmmDbContext db = _dbFactory.CreateDbContext();
        (await db.MediaItems.CountAsync(m => m.SourcePath == path)).Should().Be(0);
    }

    [Fact(DisplayName = "未启用的忽略规则不拦截：视频文件正常入队")]
    public async Task Admit_DisabledKeywordRule_DoesNotBlock()
    {
        await using (PmmDbContext seed = _dbFactory.CreateDbContext())
        {
            seed.WatchIgnoreRules.Add(new WatchIgnoreRule
            {
                Type = IgnoreRuleType.Keyword,
                Pattern = "sample",
                Enabled = false,
            });
            await seed.SaveChangesAsync();
        }
        const string path = @"F:\Downloads\Inception.2010.Sample.mkv";

        bool admitted = await _sut.AdmitAsync(path, 1, PendingFileSource.Watcher);

        admitted.Should().BeTrue("未启用规则不应拦截");
        _queue.Items.Should().ContainSingle();
    }

    // ── 测试基础设施 ──

    private sealed class RecordingQueue : IPendingFileQueue
    {
        public List<PendingFileItem> Items { get; } = new();
        public ValueTask EnqueueAsync(PendingFileItem item, CancellationToken cancellationToken = default)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
        public System.Threading.Channels.ChannelReader<PendingFileItem> Reader => throw new NotImplementedException();
    }

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
}
