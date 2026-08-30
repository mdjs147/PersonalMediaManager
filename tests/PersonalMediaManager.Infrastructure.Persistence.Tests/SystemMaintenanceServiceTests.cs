using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Maintenance;
using PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>系统维护服务 — 清空处理历史 / 重置所有配置（r4 补测：此前零独立覆盖）</summary>
/// <remarks>
/// 用 in-memory SQLite + EnsureCreated 端到端验证两条高危运维操作的边界：
///   - ClearHistory：删尽 6 张历史表（FK 顺序）并返回各表条数；不触碰配置/字典（AI 提供商 / Webhook 订阅）。
///   - ResetConfig：删尽配置表并调 IDataSeeder.SeedAsync 重种子；不触碰 Media_Item 历史。
/// IDataSeeder 用 NSubstitute mock（验证 SeedAsync 是否被调用，不依赖真实种子行为）。
/// </remarks>
public sealed class SystemMaintenanceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IDataSeeder _seeder = Substitute.For<IDataSeeder>();
    private readonly SystemMaintenanceService _sut;

    public SystemMaintenanceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new SystemMaintenanceService(_dbFactory, _seeder, NullLogger<SystemMaintenanceService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    // ---------- ClearHistory ----------

    [Fact(DisplayName = "清空历史：删尽 6 张历史表并返回各表条数，配置/字典不受影响")]
    public async Task ClearHistory_Deletes_All_History_Tables_And_Returns_Counts()
    {
        // 配置/字典行：ClearHistory 不应触碰，同时为历史行提供 FK 父
        long providerId = SeedAiProvider("qwen");
        long subId = SeedWebhookSub("hook-A");

        // 6 张历史表各 1 条
        long itemId = SeedMediaItem();
        SeedProcessStep(itemId);
        SeedAiCall(providerId, itemId);
        SeedAuditOperation();
        SeedScheduledTaskRun();
        SeedWebhookDelivery(subId);

        ClearHistoryResult r = await _sut.ClearHistoryAsync();

        r.MediaItems.Should().Be(1);
        r.ProcessSteps.Should().Be(1);
        r.AiCalls.Should().Be(1);
        r.Operations.Should().Be(1);
        r.ScheduledTaskRuns.Should().Be(1);
        r.WebhookDeliveries.Should().Be(1);

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Count().Should().Be(0);
        db.ProcessSteps.Count().Should().Be(0);
        db.AuditAiCalls.Count().Should().Be(0);
        db.AuditOperations.Count().Should().Be(0);
        db.AuditScheduledTaskRuns.Count().Should().Be(0);
        db.WebhookDeliveries.Count().Should().Be(0);

        // 配置/字典不受影响（ClearHistory 仅清历史）
        db.ParseAiProviders.Count().Should().Be(1, "ClearHistory 不动 AI 提供商配置");
        db.WebhookSubscriptions.Count().Should().Be(1, "ClearHistory 不动 Webhook 订阅配置");

        // ClearHistory 不重种子
        await _seeder.DidNotReceive().SeedAsync(Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "清空历史：空库幂等返回全 0，不抛异常")]
    public async Task ClearHistory_EmptyDb_ReturnsAllZero()
    {
        ClearHistoryResult r = await _sut.ClearHistoryAsync();

        r.MediaItems.Should().Be(0);
        r.ProcessSteps.Should().Be(0);
        r.AiCalls.Should().Be(0);
        r.Operations.Should().Be(0);
        r.ScheduledTaskRuns.Should().Be(0);
        r.WebhookDeliveries.Should().Be(0);
    }

    // ---------- ResetConfig ----------

    [Fact(DisplayName = "重置配置：清用户配置 + 重种默认分类/解析规则；保留出厂基线(忽略规则)与 SetupCompleted 标记")]
    public async Task ResetConfig_ClearsUserConfig_Reseeds_PreservesBaselineAndSetupMarker()
    {
        // 用真实 DataSeeder（而非 mock）端到端验证：重置后默认分类/解析规则恢复、出厂基线与 SetupCompleted 不丢。
        // 这是 P1 数据丢失修复的回归网——旧实现 mock seeder + 断言空表，恰好掩盖了「忽略规则/SetupCompleted 被删后不恢复」。
        DataSeeder realSeeder = new(_dbFactory, NullLogger<DataSeeder>.Instance);
        SystemMaintenanceService sut = new(_dbFactory, realSeeder, NullLogger<SystemMaintenanceService>.Instance);

        // 用户配置（应被清空）
        long catId = SeedCategory("电影");
        SeedCategoryRule(catId);
        SeedWatchFolder(@"X:\a");
        SeedParseRule();
        SeedAiProvider("qwen");
        SeedWebhookSub("hook-A");
        // SystemSetting：SetupCompleted 系统标记（应保留）+ 用户 KV（应被清）
        SeedSystemSetting("System.SetupCompleted", "true");
        SeedSystemSetting("Test_UserKey", "v");

        ResetConfigResult r = await sut.ResetConfigAsync();

        r.ReseededDefaults.Should().BeTrue();
        r.WatchIgnoreRules.Should().Be(0, "出厂忽略规则不删除，删除计数恒 0");

        using PmmDbContext db = _dbFactory.CreateDbContext();
        // 用户配置已清
        db.WatchFolders.Count().Should().Be(0);
        db.ParseAiProviders.Count().Should().Be(0);
        db.WebhookSubscriptions.Count().Should().Be(0);
        // 默认分类 + 匹配规则 + 解析规则已重种（DataSeeder 空表补齐：5 分类 + 5 匹配规则 + 默认解析规则）
        db.CategoryDefinitions.Count().Should().BeGreaterThan(0, "重置后应重种默认分类");
        db.CategoryMatchRules.Count().Should().BeGreaterThan(0, "重置后随默认分类重种匹配规则");
        db.ParseRules.Count().Should().BeGreaterThan(0, "重置后应重种默认解析规则");
        // 出厂基线保留（Watch_IgnoreRule 为 migration HasData，DataSeeder 不重建，删了无法恢复 → 不删）
        db.WatchIgnoreRules.Count().Should().BeGreaterThan(0, "出厂下载器忽略规则属基线，重置不删除");
        // SetupCompleted 标记保留（否则重启被 SetupGuard 判为未初始化拦截全部请求）
        db.SystemSettings.Any(s => s.Key == "System.SetupCompleted").Should().BeTrue("SetupCompleted 标记必须保留");
        db.SystemSettings.Any(s => s.Key == "Test_UserKey").Should().BeFalse("用户配置 KV 应被重置清除");
    }

    [Fact(DisplayName = "重置配置：不动 Media_Item 历史（与 ClearHistory 边界互斥）")]
    public async Task ResetConfig_Leaves_MediaItem_History_Untouched()
    {
        SeedMediaItem();

        await _sut.ResetConfigAsync();

        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.MediaItems.Count().Should().Be(1, "ResetConfig 仅重置配置，不动 Media_Item 历史");
        await _seeder.Received(1).SeedAsync(Arg.Any<CancellationToken>());
    }

    // ---------- seed helpers ----------

    private long SeedMediaItem()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        MediaItem item = MediaItem.CreateFixture(
            $"/tmp/{Guid.NewGuid():N}.mkv", "movie.mkv", 1024, status: MediaItemStatus.Completed);
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private void SeedProcessStep(long mediaItemId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ProcessSteps.Add(ProcessStep.CreateFixture(mediaItemId, MediaItemStatus.Parsing, DateTimeOffset.UtcNow));
        db.SaveChanges();
    }

    private long SeedAiProvider(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        ParseAiProvider p = new() { Name = name, Type = AiProviderType.OpenAiCompatible, BaseUrl = "https://x", Model = "qwen-plus", IsPrimary = true };
        db.ParseAiProviders.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private void SeedAiCall(long providerId, long mediaItemId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.AuditAiCalls.Add(new AuditAiCall
        {
            ProviderId = providerId,
            MediaItemId = mediaItemId,
            Success = true,
            LatencyMs = 1200,
            Timestamp = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedAuditOperation()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.AuditOperations.Add(new AuditOperation
        {
            Action = "Test.Operation",
            Timestamp = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedScheduledTaskRun()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.AuditScheduledTaskRuns.Add(new AuditScheduledTaskRun
        {
            JobKey = "scan.full-scan",
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = ScheduledTaskOutcome.Succeeded,
        });
        db.SaveChanges();
    }

    private long SeedWebhookSub(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        WebhookSubscription s = new()
        {
            Name = name,
            Url = $"https://hook.test/{name}",
            Events = new List<string> { "media.archived" },
            Enabled = true,
            TimeoutSeconds = 10,
        };
        db.WebhookSubscriptions.Add(s);
        db.SaveChanges();
        return s.Id;
    }

    private void SeedWebhookDelivery(long subscriptionId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            SubscriptionId = subscriptionId,
            Event = "media.archived",
            Payload = "{}",
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            RequestId = Guid.NewGuid().ToString("N"),
        });
        db.SaveChanges();
    }

    private long SeedCategory(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        CategoryDefinition c = new() { Name = name, MediaType = MediaType.Both, TargetRoot = @"X:\lib", Priority = 100 };
        db.CategoryDefinitions.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    private void SeedCategoryRule(long categoryId)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.CategoryMatchRules.Add(new CategoryMatchRule { CategoryId = categoryId, Conditions = "{}", Priority = 100 });
        db.SaveChanges();
    }

    private void SeedWatchFolder(string path)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WatchFolders.Add(new WatchFolder { Path = path, Alias = Path.GetFileName(path), Enabled = true });
        db.SaveChanges();
    }

    private void SeedParseRule()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ParseRules.Add(new ParseRule { Name = "test-rule", Pattern = @"(?<title>.+)\.(?<year>\d{4})" });
        db.SaveChanges();
    }

    private void SeedSystemSetting(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Test" });
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
}
