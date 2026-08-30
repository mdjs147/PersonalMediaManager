using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>WebhookEmitter — 事件 fan-out 到匹配订阅 + 总开关 gate + Pending 投递落库 + 入队</summary>
/// <remarks>
/// 覆盖：总开关关 / 无匹配订阅 / 订阅被禁用 → 零投递；匹配订阅 → 落 1 条 Pending（payload 含 event+requestId）+ 入队；
/// 多匹配订阅 → fan-out 多条。验证「按订阅决定是否投递」的职责（生产者侧只负责调 EmitAsync）。
/// </remarks>
public sealed class WebhookEmitterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IWebhookOutboxQueue _outbox;
    private readonly IClock _clock;
    private readonly WebhookEmitter _sut;

    public WebhookEmitterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _outbox = Substitute.For<IWebhookOutboxQueue>();
        _clock = Substitute.For<IClock>();
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));
        _sut = new WebhookEmitter(_dbFactory, _outbox, _clock, NullLogger<WebhookEmitter>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Disabled_GlobalSwitch_NoDelivery_NoEnqueue()
    {
        SeedEnabled(false);
        SeedSubscription("s1", WebhookEvents.MediaFailed, enabled: true);

        await _sut.EmitAsync(WebhookEvents.MediaFailed, new { mediaItemId = 1 });

        CountDeliveries().Should().Be(0);
        await _outbox.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoSubscriptionForEvent_NoDelivery()
    {
        SeedEnabled(true);
        SeedSubscription("s1", WebhookEvents.MediaArchived, enabled: true);   // 订阅了别的事件

        await _sut.EmitAsync(WebhookEvents.MediaFailed, new { mediaItemId = 1 });

        CountDeliveries().Should().Be(0);
        await _outbox.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledSubscription_NotDelivered()
    {
        SeedEnabled(true);
        SeedSubscription("s1", WebhookEvents.MediaFailed, enabled: false);   // 订阅了该事件但被禁用

        await _sut.EmitAsync(WebhookEvents.MediaFailed, new { mediaItemId = 1 });

        CountDeliveries().Should().Be(0);
    }

    [Fact]
    public async Task MatchingSubscription_CreatesPendingDelivery_AndEnqueues()
    {
        SeedEnabled(true);
        SeedSubscription("match", WebhookEvents.MediaFailed, enabled: true);
        SeedSubscription("other", WebhookEvents.MediaArchived, enabled: true);   // 不该收到

        await _sut.EmitAsync(WebhookEvents.MediaFailed, new { mediaItemId = 42, error = "boom" });

        using PmmDbContext db = _dbFactory.CreateDbContext();
        WebhookDelivery d = db.WebhookDeliveries.AsNoTracking().Single();
        d.Event.Should().Be(WebhookEvents.MediaFailed);
        d.Status.Should().Be(WebhookDeliveryStatus.Pending);
        d.RequestId.Should().NotBeNullOrEmpty();
        d.Payload.Should().Contain(WebhookEvents.MediaFailed).And.Contain(d.RequestId).And.Contain("42");
        await _outbox.Received(1).EnqueueAsync(d.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoMatchingSubscriptions_FanOut_TwoDeliveries()
    {
        SeedEnabled(true);
        SeedSubscription("a", WebhookEvents.MediaSkipped, enabled: true);
        SeedSubscription("b", WebhookEvents.MediaSkipped, enabled: true);

        await _sut.EmitAsync(WebhookEvents.MediaSkipped, new { mediaItemId = 7 });

        CountDeliveries().Should().Be(2);
        await _outbox.Received(2).EnqueueAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ---------- helpers ----------
    private void SeedEnabled(bool enabled)
    {
        // SystemSettingConfig.HasData 已种入 Webhook_Enabled（默认 false），EnsureCreated 后即存在 → upsert 而非 Add（否则 UNIQUE 冲突）
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? row = db.SystemSettings.FirstOrDefault(s => s.Key == "Webhook_Enabled");
        if (row is null)
            db.SystemSettings.Add(new SystemSetting { Key = "Webhook_Enabled", Value = enabled ? "true" : "false", Category = "Webhook" });
        else
            row.Value = enabled ? "true" : "false";
        db.SaveChanges();
    }

    private void SeedSubscription(string name, string @event, bool enabled)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Name = name,
            Url = "https://hooks.example.com/" + name,
            Events = [@event],
            Enabled = enabled,
            TimeoutSeconds = 10,
        });
        db.SaveChanges();
    }

    private int CountDeliveries()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.WebhookDeliveries.AsNoTracking().Count();
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
}
