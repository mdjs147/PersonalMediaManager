using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>AI 调用日志留存清理 — 按天龄 + 单 provider 行数上限双闸（r4 补测）</summary>
/// <remarks>
/// 用 in-memory SQLite + EnsureCreated 端到端验证 PurgeAsync 双闸边界：
///   - 闸一（Audit_AiCallRetentionDays）：删 Timestamp 早于 now-Days 的行；cutoff 处严格 &lt;（恰好等于 cutoff 保留）；0=停用。
///   - 闸二（Audit_AiCallMaxRowsPerProvider）：每 provider 仅保留最新 N 行，超额删最旧（OrderBy Timestamp/Id）；0=停用。
///   - 配置回退：缺失/非法/负数 → 回退默认（90 / 50000）。
///   - 多 provider 各自独立计数互不影响；返回删除总行数。
/// 时钟用固定值 FixedClock（与既有测试同风格）。Audit_AiCall 有 FK ProviderId(CASCADE)，种调用行前先种 ParseAiProvider 父行。
/// 注意：两个配置键由 SystemSettingConfig 的 HasData 种子（90 / 50000）默认写入，EnsureCreated 后已存在，
/// 故配置写入走 upsert（更新既有 seeded 行的 Value），不能 Add（否则 PK Key 冲突）。
/// </remarks>
public sealed class AiCallRetentionServiceTests : IDisposable
{
    /// <summary>固定基准时刻（UTC），所有相对天龄基于此推算</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AiCallRetentionService _sut;

    public AiCallRetentionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        _sut = new AiCallRetentionService(_dbFactory, new FixedClock(Now));
    }

    public void Dispose() => _connection.Dispose();

    // ---------- 闸一：按天龄 cutoff ----------

    [Fact(DisplayName = "闸一天龄：删超期行、留期内行；恰好等于 cutoff 的行保留（严格 <）")]
    public async Task RetentionDays_Deletes_Older_Keeps_AtOrAfter_Cutoff()
    {
        // 保留 30 天 → cutoff = Now-30d；其它闸停用，隔离仅测天龄闸
        SetSetting(AiCallRetentionService.RetentionDaysKey, "30");
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "0");
        long pid = SeedAiProvider("qwen");

        DateTimeOffset cutoff = Now.AddDays(-30);
        long olderId = SeedAiCall(pid, cutoff.AddSeconds(-1)); // 早于 cutoff 1 秒 → 删
        long atCutoffId = SeedAiCall(pid, cutoff);              // 恰好等于 cutoff → 保留（严格 <）
        long newerId = SeedAiCall(pid, cutoff.AddSeconds(1));  // 晚于 cutoff → 保留

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(1, "仅早于 cutoff 的 1 行被删");
        RemainingIds().Should().BeEquivalentTo(new[] { atCutoffId, newerId });
        RemainingIds().Should().NotContain(olderId);
    }

    [Fact(DisplayName = "闸一天龄：配置 0 = 停用该闸，超期行也不删")]
    public async Task RetentionDays_Zero_Disables_Gate()
    {
        // 天龄闸停用 + 行数闸停用 → 整体应无删除
        SetSetting(AiCallRetentionService.RetentionDaysKey, "0");
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "0");
        long pid = SeedAiProvider("qwen");

        // 故意种一条「上古」行：若天龄闸生效必删，验证 0 确实停用
        SeedAiCall(pid, Now.AddDays(-9999));
        SeedAiCall(pid, Now);

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(0, "天龄闸置 0 停用，超期行也保留");
        RowCount().Should().Be(2);
    }

    [Fact(DisplayName = "闸一天龄：非法/负数配置 → 回退默认 90 天")]
    public async Task RetentionDays_Invalid_Or_Negative_Falls_Back_To_Default_90()
    {
        // 非法值（非数字）走天龄闸，负数走行数闸，两者都应触发回退；
        // 行数闸默认 50000 远大于种入行数，不会误删，故净效果仅天龄默认 90 生效。
        SetSetting(AiCallRetentionService.RetentionDaysKey, "abc");   // 非法 → 回退 90
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "-5"); // 负数 → 回退 50000（不删）
        long pid = SeedAiProvider("qwen");

        long oldId = SeedAiCall(pid, Now.AddDays(-100)); // > 90 天 → 应被默认天龄闸删
        long keepId = SeedAiCall(pid, Now.AddDays(-10)); // < 90 天 → 保留

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(1, "天龄非法值回退默认 90，超 90 天 1 行被删");
        RemainingIds().Should().BeEquivalentTo(new[] { keepId });
        RemainingIds().Should().NotContain(oldId);
    }

    [Fact(DisplayName = "闸一天龄：缺失配置（删除 seeded 行）→ 回退默认 90 天")]
    public async Task RetentionDays_Missing_Falls_Back_To_Default_90()
    {
        // 删掉 seeded 的天龄键模拟「配置缺失」；行数键也删避免其干扰（缺失回退 50000 不删）
        RemoveSetting(AiCallRetentionService.RetentionDaysKey);
        RemoveSetting(AiCallRetentionService.MaxRowsPerProviderKey);
        long pid = SeedAiProvider("qwen");

        long oldId = SeedAiCall(pid, Now.AddDays(-91)); // 超默认 90 → 删
        long keepId = SeedAiCall(pid, Now.AddDays(-89)); // 不足 90 → 留

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(1, "配置缺失回退默认 90");
        RemainingIds().Should().BeEquivalentTo(new[] { keepId });
        RemainingIds().Should().NotContain(oldId);
    }

    // ---------- 闸二：单 provider 行数上限 ----------

    [Fact(DisplayName = "闸二行数：恰好等于上限不删")]
    public async Task MaxRows_Equal_To_Limit_Deletes_Nothing()
    {
        SetSetting(AiCallRetentionService.RetentionDaysKey, "0"); // 停天龄闸，隔离仅测行数闸
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "3");
        long pid = SeedAiProvider("qwen");

        // 恰好 3 行（== 上限）→ 不应删
        for (int i = 0; i < 3; i++) SeedAiCall(pid, Now.AddMinutes(-i));

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(0, "行数恰好等于上限不触发清理");
        RowCount().Should().Be(3);
    }

    [Fact(DisplayName = "闸二行数：超上限 1 → 删最旧 1 行")]
    public async Task MaxRows_Over_By_One_Deletes_Oldest()
    {
        SetSetting(AiCallRetentionService.RetentionDaysKey, "0");
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "3");
        long pid = SeedAiProvider("qwen");

        // 4 行，上限 3 → 删最旧 1（Timestamp 最小者）
        long oldestId = SeedAiCall(pid, Now.AddMinutes(-30));
        long b = SeedAiCall(pid, Now.AddMinutes(-20));
        long c = SeedAiCall(pid, Now.AddMinutes(-10));
        long d = SeedAiCall(pid, Now);

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(1);
        RemainingIds().Should().BeEquivalentTo(new[] { b, c, d });
        RemainingIds().Should().NotContain(oldestId);
    }

    [Fact(DisplayName = "闸二行数：远超上限 → 删到剩上限条（保留最新 N）")]
    public async Task MaxRows_Far_Over_Deletes_Down_To_Limit()
    {
        SetSetting(AiCallRetentionService.RetentionDaysKey, "0");
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "2");
        long pid = SeedAiProvider("qwen");

        // 种 10 行，时间递增；上限 2 → 删 8，保留最新 2（Now-1min, Now-0min）
        var ids = new List<long>();
        for (int i = 0; i < 10; i++) ids.Add(SeedAiCall(pid, Now.AddMinutes(-(9 - i)))); // 越后越新
        long newest1 = ids[8];
        long newest2 = ids[9];

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(8, "10 行删到剩上限 2");
        RemainingIds().Should().BeEquivalentTo(new[] { newest1, newest2 });
    }

    [Fact(DisplayName = "闸二行数：配置 0 = 停用该闸（即便远超也不删）")]
    public async Task MaxRows_Zero_Disables_Gate()
    {
        SetSetting(AiCallRetentionService.RetentionDaysKey, "0"); // 天龄也停
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "0");
        long pid = SeedAiProvider("qwen");

        for (int i = 0; i < 5; i++) SeedAiCall(pid, Now.AddMinutes(-i));

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(0, "行数闸置 0 停用");
        RowCount().Should().Be(5);
    }

    // ---------- 多 provider 独立计数 ----------

    [Fact(DisplayName = "多 provider：行数上限各自独立计数，互不影响")]
    public async Task MaxRows_Counts_Per_Provider_Independently()
    {
        SetSetting(AiCallRetentionService.RetentionDaysKey, "0");
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "2");
        long pidA = SeedAiProvider("qwen");
        long pidB = SeedAiProvider("deepseek");

        // A：4 行（超 2 → 删 2）；B：2 行（== 2 → 不删）
        long aOld1 = SeedAiCall(pidA, Now.AddMinutes(-40));
        long aOld2 = SeedAiCall(pidA, Now.AddMinutes(-30));
        long aKeep1 = SeedAiCall(pidA, Now.AddMinutes(-20));
        long aKeep2 = SeedAiCall(pidA, Now.AddMinutes(-10));
        long bKeep1 = SeedAiCall(pidB, Now.AddMinutes(-15));
        long bKeep2 = SeedAiCall(pidB, Now.AddMinutes(-5));

        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(2, "仅 provider A 超额删 2；B 恰好等于上限不删");
        RemainingIds().Should().BeEquivalentTo(new[] { aKeep1, aKeep2, bKeep1, bKeep2 });
        RemainingIds().Should().NotContain(aOld1);
        RemainingIds().Should().NotContain(aOld2);
    }

    // ---------- 双闸协同 + 返回值 ----------

    [Fact(DisplayName = "双闸协同：天龄删超期 + 行数删超额，返回删除总条数正确")]
    public async Task BothGates_Combine_And_Return_Total_Deleted()
    {
        // 天龄 30 天 + 行数上限 2 同时生效
        SetSetting(AiCallRetentionService.RetentionDaysKey, "30");
        SetSetting(AiCallRetentionService.MaxRowsPerProviderKey, "2");
        long pid = SeedAiProvider("qwen");

        // 2 行超期（被天龄闸删）
        SeedAiCall(pid, Now.AddDays(-100));
        SeedAiCall(pid, Now.AddDays(-50));
        // 期内 4 行：天龄闸删完后剩这 4 行 → 行数闸上限 2 再删最旧 2，保留最新 2
        SeedAiCall(pid, Now.AddDays(-4));
        SeedAiCall(pid, Now.AddDays(-3));
        long keep1 = SeedAiCall(pid, Now.AddDays(-2));
        long keep2 = SeedAiCall(pid, Now.AddDays(-1));

        int deleted = await _sut.PurgeAsync();

        // 天龄删 2 + 行数删 2 = 4
        deleted.Should().Be(4, "天龄删 2 + 行数删 2");
        RemainingIds().Should().BeEquivalentTo(new[] { keep1, keep2 });
    }

    [Fact(DisplayName = "空库：无任何行，返回 0 且不抛异常")]
    public async Task EmptyDb_Returns_Zero()
    {
        int deleted = await _sut.PurgeAsync();

        deleted.Should().Be(0);
        RowCount().Should().Be(0);
    }

    // ---------- seed / 查询 helpers ----------

    /// <summary>upsert System_Setting（seeded 键已存在则更新 Value，否则新增）</summary>
    private void SetSetting(string key, string value)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? existing = db.SystemSettings.FirstOrDefault(s => s.Key == key);
        if (existing is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Audit" });
        }
        else
        {
            existing.Value = value;
        }
        db.SaveChanges();
    }

    /// <summary>删除 System_Setting 行（模拟配置缺失）</summary>
    private void RemoveSetting(string key)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? existing = db.SystemSettings.FirstOrDefault(s => s.Key == key);
        if (existing is not null)
        {
            db.SystemSettings.Remove(existing);
            db.SaveChanges();
        }
    }

    private long SeedAiProvider(string name)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        ParseAiProvider p = new() { Name = name, Type = AiProviderType.OpenAiCompatible, BaseUrl = "https://x", Model = "qwen-plus", IsPrimary = true };
        db.ParseAiProviders.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    /// <summary>种一条 AI 调用行（指定 Timestamp），返回其 Id</summary>
    private long SeedAiCall(long providerId, DateTimeOffset timestamp)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        AuditAiCall call = new()
        {
            ProviderId = providerId,
            Success = true,
            LatencyMs = 1000,
            Timestamp = timestamp,
        };
        db.AuditAiCalls.Add(call);
        db.SaveChanges();
        return call.Id;
    }

    private int RowCount()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.AuditAiCalls.Count();
    }

    private List<long> RemainingIds()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        return db.AuditAiCalls.AsNoTracking().Select(a => a.Id).ToList();
    }

    /// <summary>固定时钟：UtcNow 恒返回构造时刻</summary>
    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) { UtcNow = utcNow; }
        public DateTimeOffset UtcNow { get; }
    }

    /// <summary>共享单一 in-memory SQLite 连接的 DbContext 工厂（含时间戳/行版本拦截器）</summary>
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
