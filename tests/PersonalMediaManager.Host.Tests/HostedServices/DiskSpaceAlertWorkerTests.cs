using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>DiskSpaceAlertWorker — 归档盘剩余百分比周期巡检触发 disk.low</summary>
/// <remarks>
/// SQLite in-memory 真实 DbContext + Substitute IAlertService + 注入盘容量探测函数（隔离真实 DriveInfo），
/// 直连 internal SweepAsync 断言（不依赖 PeriodicTimer 时序）。
/// 断言抑制键沿用 ArchiveService 口径「disk.low:{盘根}」，与归档侧共享抑制窗口。
/// </remarks>
public sealed class DiskSpaceAlertWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IAlertService _alert = Substitute.For<IAlertService>();

    public DiskSpaceAlertWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "剩余低于 critical → 发严重 disk.low（抑制键=disk.low:盘根）")]
    public async Task Below_Critical_Raises_Critical_DiskLow()
    {
        SeedCategory(@"Q:\media\电影");
        SeedThresholds(warn: 10, critical: 5);
        DiskSpaceAlertWorker sut = NewWorker(_ => (TotalBytes: 1000L, AvailableBytes: 30L)); // 剩 3%

        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(1);
        await _alert.Received(1).RaiseAsync(@"disk.low:Q:\", "disk.low", Arg.Any<object>(), Arg.Any<CancellationToken>());
        PayloadJson().Should().Contain("\"severity\":\"critical\"").And.Contain("\"thresholdPercent\":5");
    }

    [Fact(DisplayName = "剩余介于 critical 与 warn 之间 → 发警告 disk.low")]
    public async Task Between_Critical_And_Warn_Raises_Warn_DiskLow()
    {
        SeedCategory(@"Q:\media\剧集");
        SeedThresholds(warn: 10, critical: 5);
        DiskSpaceAlertWorker sut = NewWorker(_ => (TotalBytes: 1000L, AvailableBytes: 70L)); // 剩 7%

        await sut.SweepAsync(CancellationToken.None);

        await _alert.Received(1).RaiseAsync(@"disk.low:Q:\", "disk.low", Arg.Any<object>(), Arg.Any<CancellationToken>());
        PayloadJson().Should().Contain("\"severity\":\"warn\"").And.Contain("\"thresholdPercent\":10");
    }

    [Fact(DisplayName = "剩余高于 warn → 不发告警")]
    public async Task Above_Warn_No_Alert()
    {
        SeedCategory(@"Q:\media\电影");
        SeedThresholds(warn: 10, critical: 5);
        DiskSpaceAlertWorker sut = NewWorker(_ => (TotalBytes: 1000L, AvailableBytes: 500L)); // 剩 50%

        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(1);
        await _alert.DidNotReceiveWithAnyArgs().RaiseAsync(default!, default!, default!, default);
    }

    [Fact(DisplayName = "双阈值均为 0（检查关闭）→ 不读盘不告警")]
    public async Task Both_Thresholds_Zero_Skips_Probe_And_Alert()
    {
        SeedCategory(@"Q:\media\电影");
        SeedThresholds(warn: 0, critical: 0);
        bool probeCalled = false;
        DiskSpaceAlertWorker sut = NewWorker(_ => { probeCalled = true; return (1000L, 1L); });

        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(0);
        probeCalled.Should().BeFalse("双阈值关闭时无需读盘");
        await _alert.DidNotReceiveWithAnyArgs().RaiseAsync(default!, default!, default!, default);
    }

    [Fact(DisplayName = "阈值缺失回退默认 warn=10/critical=5")]
    public async Task Missing_Thresholds_Fallback_Defaults()
    {
        SeedCategory(@"Q:\media\电影");
        RemoveThresholdRows(); // 删掉 EnsureCreated 落下的 HasData 种子行，真正走「缺失回退默认」路径
        DiskSpaceAlertWorker sut = NewWorker(_ => (TotalBytes: 1000L, AvailableBytes: 80L)); // 剩 8% < 默认 warn 10

        await sut.SweepAsync(CancellationToken.None);

        await _alert.Received(1).RaiseAsync(@"disk.low:Q:\", "disk.low", Arg.Any<object>(), Arg.Any<CancellationToken>());
        PayloadJson().Should().Contain("\"severity\":\"warn\"");
    }

    [Fact(DisplayName = "盘不可读（网络盘/未就绪）→ 跳过不告警")]
    public async Task Unreadable_Drive_Skipped_No_Alert()
    {
        SeedCategory(@"Q:\media\电影");
        SeedThresholds(warn: 10, critical: 5);
        DiskSpaceAlertWorker sut = NewWorker(_ => null);

        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(0);
        await _alert.DidNotReceiveWithAnyArgs().RaiseAsync(default!, default!, default!, default);
    }

    [Fact(DisplayName = "多分类同盘根去重：只探测/告警一次")]
    public async Task Same_Drive_Root_Deduplicated()
    {
        SeedCategory(@"Q:\media\电影");
        SeedCategory(@"Q:\media\剧集");
        SeedCategory(@"q:\anime"); // 大小写不同也归并
        SeedThresholds(warn: 10, critical: 5);
        int probeCalls = 0;
        DiskSpaceAlertWorker sut = NewWorker(_ => { probeCalls++; return (1000L, 30L); });

        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(1);
        probeCalls.Should().Be(1, "同盘根去重后只读一次");
        await _alert.ReceivedWithAnyArgs(1).RaiseAsync(default!, default!, default!, default);
    }

    [Fact(DisplayName = "无分类 → 空转不告警")]
    public async Task No_Categories_Noop()
    {
        SeedThresholds(warn: 10, critical: 5);
        DiskSpaceAlertWorker sut = NewWorker(_ => (TotalBytes: 1000L, AvailableBytes: 1L));

        int probed = await sut.SweepAsync(CancellationToken.None);

        probed.Should().Be(0);
        await _alert.DidNotReceiveWithAnyArgs().RaiseAsync(default!, default!, default!, default);
    }

    // ---------- helpers ----------

    private DiskSpaceAlertWorker NewWorker(Func<string, (long TotalBytes, long AvailableBytes)?> probe)
        => new(_dbFactory, _alert, NullLogger<DiskSpaceAlertWorker>.Instance, TimeSpan.FromMinutes(5), probe);

    private void SeedCategory(string targetRoot)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.CategoryDefinitions.Add(new CategoryDefinition
        {
            Name = $"分类-{Guid.NewGuid():N}",
            MediaType = MediaType.Movie,
            TargetRoot = targetRoot,
        });
        ctx.SaveChanges();
    }

    /// <summary>EnsureCreated 已按 HasData 落阈值种子行（10/5），此处 upsert 覆盖避免 UNIQUE 冲突</summary>
    private void SeedThresholds(int warn, int critical)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        Upsert(ctx, "Archive_DiskWarnPercent", warn.ToString());
        Upsert(ctx, "Archive_DiskCriticalPercent", critical.ToString());
        ctx.SaveChanges();
    }

    private static void Upsert(PmmDbContext ctx, string key, string value)
    {
        SystemSetting? row = ctx.SystemSettings.FirstOrDefault(s => s.Key == key);
        if (row is not null)
            row.Value = value;
        else
            ctx.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Archive" });
    }

    private void RemoveThresholdRows()
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.SystemSettings.RemoveRange(ctx.SystemSettings
            .Where(s => s.Key == "Archive_DiskWarnPercent" || s.Key == "Archive_DiskCriticalPercent"));
        ctx.SaveChanges();
    }

    /// <summary>取唯一一次 RaiseAsync 的 data 参数序列化为 JSON（断言 severity / threshold 字段）</summary>
    private string PayloadJson()
    {
        object payload = (object)_alert.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAlertService.RaiseAsync))
            .GetArguments()[2]!;
        return JsonSerializer.Serialize(payload);
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
