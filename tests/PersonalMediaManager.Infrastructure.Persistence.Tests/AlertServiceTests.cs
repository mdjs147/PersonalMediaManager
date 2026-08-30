using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>AlertService — 抑制窗口（System_AlertSuppressMinutes）+ 复用 IWebhookEmitter 发送</summary>
public sealed class AlertServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly AlertSuppressionState _suppression = new();
    private readonly IWebhookEmitter _emitter = Substitute.For<IWebhookEmitter>();

    public AlertServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();   // 种子含 System_AlertSuppressMinutes=60
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task RaiseAsync_FirstCall_InvokesEmitter()
    {
        await NewSut().RaiseAsync("disk.low:D", WebhookEvents.DiskLow, new { x = 1 });

        await _emitter.Received(1).EmitAsync(WebhookEvents.DiskLow, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RaiseAsync_WithinWindow_SecondCall_Suppressed()
    {
        AlertService sut = NewSut();   // 固定时钟：两次调用同一时刻 → 60min 窗口内
        await sut.RaiseAsync("disk.low:D", WebhookEvents.DiskLow, new { });
        await sut.RaiseAsync("disk.low:D", WebhookEvents.DiskLow, new { });

        await _emitter.Received(1).EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RaiseAsync_DifferentKeys_NotSuppressed()
    {
        AlertService sut = NewSut();
        await sut.RaiseAsync("disk.low:C", WebhookEvents.DiskLow, new { });
        await sut.RaiseAsync("disk.low:D", WebhookEvents.DiskLow, new { });   // 不同盘根 → 各发一次

        await _emitter.Received(2).EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RaiseAsync_SuppressZero_AlwaysEmits()
    {
        SeedSuppressMinutes(0);   // 0 = 不抑制
        AlertService sut = NewSut();
        await sut.RaiseAsync("k", WebhookEvents.AiAllUnavailable, new { });
        await sut.RaiseAsync("k", WebhookEvents.AiAllUnavailable, new { });

        await _emitter.Received(2).EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RaiseAsync_EmitterThrows_Swallowed_NotPropagated()
    {
        _emitter.EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        // 不抛 = 测试通过（告警失败不得阻断触发它的主流程）
        await NewSut().RaiseAsync("k", WebhookEvents.DiskLow, new { });
    }

    [Fact]
    public async Task RaiseAsync_SuppressMinutes_AboveCap_ClampedToSevenDays()
    {
        // 误填超大抑制分钟（>10080）须被钳到 7 天上限，而非进程内永久哑火：
        // 用可推进时钟，第二次在「刚过 7 天上限」时刻触发——钳位后窗口已过 → 再发；未钳位（~69 天）则仍被抑制。
        SeedSuppressMinutes(100_000);
        MutableClock clock = new(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));
        AlertService sut = new(_emitter, _dbFactory, clock, new AlertSuppressionState(), NullLogger<AlertService>.Instance);

        await sut.RaiseAsync("k", WebhookEvents.DiskLow, new { });   // 首次 → 发
        clock.UtcNow = clock.UtcNow.AddMinutes(10_081);             // 刚过 7 天上限
        await sut.RaiseAsync("k", WebhookEvents.DiskLow, new { });   // 钳到 10080 → 窗口已过 → 再发

        await _emitter.Received(2).EmitAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    private AlertService NewSut() => new(
        _emitter, _dbFactory, _clock, _suppression, NullLogger<AlertService>.Instance);

    private void SeedSuppressMinutes(int minutes)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? s = db.SystemSettings.FirstOrDefault(x => x.Key == "System_AlertSuppressMinutes");
        if (s is null) db.SystemSettings.Add(new SystemSetting { Key = "System_AlertSuppressMinutes", Value = minutes.ToString(), Category = "Alert" });
        else s.Value = minutes.ToString();
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

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; set; }
    }
}

/// <summary>AlertSuppressionState — 纯内存抑制表（首次/窗口内/窗口外/多 key 独立）</summary>
public sealed class AlertSuppressionStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstFire_True_ThenSuppressedWithinWindow_ThenFiresAfterWindow()
    {
        AlertSuppressionState s = new();
        TimeSpan window = TimeSpan.FromMinutes(60);

        s.ShouldFire("k", window, T0).Should().BeTrue();                       // 首次
        s.ShouldFire("k", window, T0.AddMinutes(30)).Should().BeFalse();       // 窗口内
        s.ShouldFire("k", window, T0.AddMinutes(61)).Should().BeTrue();        // 窗口外重新可发
    }

    [Fact]
    public void DifferentKeys_Independent()
    {
        AlertSuppressionState s = new();
        TimeSpan window = TimeSpan.FromMinutes(60);

        s.ShouldFire("a", window, T0).Should().BeTrue();
        s.ShouldFire("b", window, T0).Should().BeTrue();   // 不同 key 互不抑制
    }
}
