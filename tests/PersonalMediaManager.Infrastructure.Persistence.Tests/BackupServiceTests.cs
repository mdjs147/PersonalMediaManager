using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Maintenance;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>BackupService — 定时备份失败时发 backup.failed Webhook（补「失败仅落 Audit 行」的独立告警盲区）</summary>
/// <remarks>
/// 用「db 路径指向目录」强制 CreateAsync 的 VACUUM 打不开库 → 必失败；IsEnabled / 设置 / 订阅读自独立的内存测试库（_dbFactory）。
/// </remarks>
public sealed class BackupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _rootDir;
    private readonly AppPaths _paths;
    private readonly IWebhookOutboxQueue _outbox = Substitute.For<IWebhookOutboxQueue>();
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    public BackupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _rootDir = Path.Combine(Path.GetTempPath(), $"pmm-backupsvc-{Guid.NewGuid():N}");
        _paths = AppPaths.ForRoot(_rootDir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ScheduledFailure_Emits_BackupFailed_Webhook_When_Enabled_And_Subscribed()
    {
        SeedSetting("Backup_Enabled", "true");
        SeedSetting("Webhook_Enabled", "true");
        SeedSubscription("hook", "backup.failed");

        // _dbLocation 指向目录（无法作为 SQLite 库打开）→ CreateAsync 的 VACUUM 必失败
        BackupService sut = NewSut(badDbPath: _rootDir);

        Func<Task> act = () => sut.RunScheduledAsync();
        await act.Should().ThrowAsync<Exception>("备份失败仍需上抛，让 Recorder 记 Audit Failed");

        using PmmDbContext db = _dbFactory.CreateDbContext();
        WebhookDelivery? d = db.WebhookDeliveries.AsNoTracking().FirstOrDefault();
        d.Should().NotBeNull("订阅了 backup.failed → 应写一条投递");
        d!.Event.Should().Be("backup.failed");
        d.Payload.Should().Contain("backup.failed");
        await _outbox.Received().EnqueueAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduledFailure_NoWebhook_When_Globally_Disabled()
    {
        SeedSetting("Backup_Enabled", "true");
        SeedSetting("Webhook_Enabled", "false");
        SeedSubscription("hook", "backup.failed");

        BackupService sut = NewSut(badDbPath: _rootDir);

        Func<Task> act = () => sut.RunScheduledAsync();
        await act.Should().ThrowAsync<Exception>();

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookDeliveries.AsNoTracking().Should().BeEmpty("Webhook 总开关关 → 不产生投递");
        await _outbox.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduledFailure_NoWebhook_When_NotSubscribedToEvent()
    {
        SeedSetting("Backup_Enabled", "true");
        SeedSetting("Webhook_Enabled", "true");
        SeedSubscription("hook", "media.archived"); // 只订阅归档，未订阅 backup.failed

        BackupService sut = NewSut(badDbPath: _rootDir);

        Func<Task> act = () => sut.RunScheduledAsync();
        await act.Should().ThrowAsync<Exception>();

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookDeliveries.AsNoTracking().Should().BeEmpty("未订阅 backup.failed → 无投递");
    }

    [Fact]
    public async Task Disabled_Backup_Skips_Without_Failure_Or_Webhook()
    {
        SeedSetting("Backup_Enabled", "false");
        BackupService sut = NewSut(badDbPath: _rootDir);

        BackupResult r = await sut.RunScheduledAsync();

        r.Performed.Should().BeFalse("未启用直接跳过，不触发 CreateAsync");
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookDeliveries.AsNoTracking().Should().BeEmpty();
    }

    // ---------- helpers ----------

    private BackupService NewSut(string badDbPath) => new(
        _dbFactory, _paths, new DatabaseFileLocation(badDbPath), _outbox, _clock, NullLogger<BackupService>.Instance);

    private void SeedSetting(string key, string value)
    {
        // EnsureCreated 已种子部分 System_Setting（Backup_Enabled / Webhook_Enabled 等），upsert 而非 Add 避免 UNIQUE 冲突
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? s = db.SystemSettings.FirstOrDefault(x => x.Key == key);
        if (s is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        else s.Value = value;
        db.SaveChanges();
    }

    private void SeedSubscription(string name, string @event)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Name = name,
            Url = "https://example.com/hook",
            Events = new List<string> { @event },
            Enabled = true,
            TimeoutSeconds = 10,
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
            opts.AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor());
            return new PmmDbContext(opts.Options);
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }
}
