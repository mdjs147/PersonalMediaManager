using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Dashboard;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Dashboard;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>HealthCheckService — 4 项并行 + 各项独立超时 + 异常归 fail</summary>
public sealed class HealthCheckServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly string _rootDir;
    private readonly AppPaths _paths;
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);
    private readonly ITmdbConnectivityTester _tmdbTester = Substitute.For<ITmdbConnectivityTester>();
    private readonly IAiProviderResolver _aiResolver = Substitute.For<IAiProviderResolver>();
    private readonly IAiProviderTester _aiTester = Substitute.For<IAiProviderTester>();
    private readonly IProtectedFieldService _protector = Substitute.For<IProtectedFieldService>();

    public HealthCheckServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _rootDir = Path.Combine(Path.GetTempPath(), $"pmm-healthcheck-{Guid.NewGuid():N}");
        _paths = AppPaths.ForRoot(_rootDir);
        // 默认创建 pmm.db 文件让 SQLite 检查不报 fail
        File.WriteAllBytes(_paths.DbFile, new byte[1024]);

        _protector.Unprotect(Arg.Any<string>()).Returns("plain-api-key");
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CheckAsync_Returns_Eight_Entries_In_Fixed_Order()
    {
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks.Should().HaveCount(8);
        data.Checks[0].Name.Should().Be("Kestrel");
        data.Checks[1].Name.Should().Be("SQLite (pmm.db)");
        data.Checks[2].Name.Should().Be("TMDB");
        data.Checks[3].Name.Should().Be("AI 主提供商");
        data.Checks[4].Name.Should().Be("备份");
        data.Checks[5].Name.Should().Be("归档磁盘");
        data.Checks[6].Name.Should().Be("网络盘");
        data.Checks[7].Name.Should().Be("AI 可用性");
    }

    [Fact]
    public async Task Kestrel_Returns_Ok_With_Uptime_Detail()
    {
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry kestrel = data.Checks[0];
        kestrel.Status.Should().Be("ok");
        kestrel.Detail.Should().StartWith("上线 ");
        kestrel.LatencyMs.Should().BeNull();
    }

    [Fact]
    public async Task Sqlite_Returns_Ok_With_Size_Formatted()
    {
        // 重写 1 MB 让格式化进 MB 段
        File.WriteAllBytes(_paths.DbFile, new byte[1024 * 1024]);
        StubTmdbOk(); StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry sqlite = data.Checks[1];
        sqlite.Status.Should().Be("ok");
        sqlite.Detail.Should().EndWith(" MB");
    }

    [Fact]
    public async Task Sqlite_Missing_Db_File_Returns_Fail()
    {
        File.Delete(_paths.DbFile);
        StubTmdbOk(); StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry sqlite = data.Checks[1];
        sqlite.Status.Should().Be("fail");
        sqlite.Detail.Should().Contain("不存在");
    }

    [Fact]
    public async Task Tmdb_No_Setting_Returns_Off()
    {
        StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry tmdb = data.Checks[2];
        tmdb.Status.Should().Be("off");
        tmdb.Detail.Should().Contain("未配置");
    }

    [Fact]
    public async Task Tmdb_Connectivity_Ok_Returns_Ok_With_Latency()
    {
        SeedTmdbSetting();
        _tmdbTester.TestAsync("plain-api-key", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbConnectivityTestResult(true, 200, 215.3, null));
        StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry tmdb = data.Checks[2];
        tmdb.Status.Should().Be("ok");
        tmdb.Detail.Should().Contain("200 OK");
        tmdb.LatencyMs.Should().Be(215);
    }

    [Fact]
    public async Task Tmdb_Connectivity_Failure_Returns_Fail()
    {
        SeedTmdbSetting();
        _tmdbTester.TestAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbConnectivityTestResult(false, 401, 50.0, "Unauthorized"));
        StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[2].Status.Should().Be("fail");
        data.Checks[2].Detail.Should().Contain("Unauthorized");
    }

    [Fact]
    public async Task Ai_No_Provider_Returns_Off()
    {
        StubTmdbOk();
        _aiResolver.ResolveOrderedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AiProviderResolution>());

        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[3].Status.Should().Be("off");
    }

    [Fact]
    public async Task Ai_Primary_Ok_Returns_Ok_With_ProviderName()
    {
        StubTmdbOk();
        _aiResolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new AiProviderResolution(1, AiProviderType.OpenAiCompatible, "Qwen 主", IsPrimary: true,
                new AiProviderEndpoint("http://api.qwen.com", "key", "qwen-plus", 30)),
        });
        _aiTester.TestAsync(Arg.Any<AiProviderType>(), Arg.Any<string>(), Arg.Any<string?>(),
                            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new AiProviderTestResult(true, 200, 1240, null, null));

        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry ai = data.Checks[3];
        ai.Status.Should().Be("ok");
        ai.Detail.Should().Contain("Qwen 主");
        ai.LatencyMs.Should().Be(1240);
    }

    [Fact]
    public async Task Ai_Tester_Throws_Returns_Fail_Not_Crash()
    {
        StubTmdbOk();
        _aiResolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new AiProviderResolution(1, AiProviderType.OpenAiCompatible, "Qwen", IsPrimary: true,
                new AiProviderEndpoint("http://api.qwen.com", "key", "qwen-plus", 30)),
        });
        _aiTester.TestAsync(Arg.Any<AiProviderType>(), Arg.Any<string>(), Arg.Any<string?>(),
                            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("DNS 失败"));

        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[3].Status.Should().Be("fail", "异常归 fail，绝不让一个检查项炸掉整个聚合");
        data.Checks.Should().HaveCount(8, "其他 7 项仍然要返回");
    }

    [Fact]
    public async Task Tmdb_Tester_Hangs_Times_Out_At_Per_Check_Budget()
    {
        SeedTmdbSetting();
        // 模拟挂起：返回永不完成的 Task
        TaskCompletionSource<TmdbConnectivityTestResult> never = new();
        _tmdbTester.TestAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(never.Task);
        StubAiOff();

        HealthCheckService sut = NewSut();
        // 测试默认 3s 超时偏长 → 全套包一个 5s 整体超时以防测试卡死
        using CancellationTokenSource overall = new(TimeSpan.FromSeconds(5));
        DashboardHealthResponse data = await sut.CheckAsync(overall.Token);

        data.Checks[2].Status.Should().Be("fail");
        data.Checks[2].Detail.Should().Contain("超时");
    }

    [Fact]
    public async Task Backup_Not_Enabled_Returns_Off()
    {
        SeedBackupEnabled(false);
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry backup = data.Checks[4];
        backup.Name.Should().Be("备份");
        backup.Status.Should().Be("off");
    }

    [Fact]
    public async Task Backup_Enabled_Fresh_Returns_Ok()
    {
        SeedBackupEnabled(true);
        CreateBackupFile("pmm-backup-20260606040000.zip", _clock.UtcNow);
        StubTmdbOk(); StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[4].Status.Should().Be("ok");
        data.Checks[4].Detail.Should().StartWith("最近备份 ");
    }

    [Fact]
    public async Task Backup_Enabled_But_Stale_Returns_Fail()
    {
        SeedBackupEnabled(true);
        CreateBackupFile("pmm-backup-20260601040000.zip", _clock.UtcNow - TimeSpan.FromHours(40));
        StubTmdbOk(); StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[4].Status.Should().Be("fail");
        data.Checks[4].Detail.Should().Contain("疑似失败");
    }

    [Fact]
    public async Task Backup_Enabled_But_No_Artifact_Returns_Fail()
    {
        SeedBackupEnabled(true);
        StubTmdbOk(); StubAiOff();

        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[4].Status.Should().Be("fail");
        data.Checks[4].Detail.Should().Contain("尚无备份");
    }

    // ---------- 新增 3 项：归档磁盘 / 网络盘 / AI 可用性 ----------

    [Fact]
    public async Task Disk_No_Category_Returns_Off()
    {
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        HealthCheckEntry disk = data.Checks[5];
        disk.Name.Should().Be("归档磁盘");
        disk.Status.Should().Be("off");
        disk.Detail.Should().Contain("未配置归档分类");
    }

    [Fact]
    public async Task NetworkShares_None_Returns_Off()
    {
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[6].Name.Should().Be("网络盘");
        data.Checks[6].Status.Should().Be("off");
    }

    [Fact]
    public async Task NetworkShares_All_Reachable_Returns_Ok()
    {
        SeedNetworkShare(@"\\nas\media", _clock.UtcNow);   // 刚刷新 → 2 分钟内可达
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[6].Status.Should().Be("ok");
        data.Checks[6].Detail.Should().Contain("全部可达");
    }

    [Fact]
    public async Task NetworkShares_Unreachable_Returns_Warn()
    {
        SeedNetworkShare(@"\\nas\gone", lastReachableAt: null);   // 从未可达
        StubTmdbOk(); StubAiOff();
        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[6].Status.Should().Be("warn");
        data.Checks[6].Detail.Should().Contain("不可达");
    }

    [Fact]
    public async Task AiAvailability_No_Enabled_Provider_Returns_Off()
    {
        StubTmdbOk(); StubAiOff();   // 未种任何 Parse_AiProvider
        DashboardHealthResponse data = await NewSut().CheckAsync();

        data.Checks[7].Name.Should().Be("AI 可用性");
        data.Checks[7].Status.Should().Be("off");
        data.Checks[7].Detail.Should().Contain("未启用");
    }

    // ---------- 读侧兜底：百分比钳位 [0,100] ----------

    [Theory]
    [InlineData("200", 100)]    // >100 钳到上限（防写侧绕过的越界值把磁盘检查配成恒 fail）
    [InlineData("101", 100)]
    [InlineData("-5", 0)]       // <0 钳到下限
    [InlineData("37", 37)]      // 区间内原样
    [InlineData("abc", 10)]     // 非法回退默认（默认本身在区间内）
    [InlineData(null, 10)]      // 缺失回退默认
    public void ParsePercentOr_Clamps_To_Zero_Hundred(string? raw, int expected)
        => HealthCheckService.ParsePercentOr(raw, 10).Should().Be(expected);

    // ---------- helpers ----------

    private HealthCheckService NewSut() => new(
        _dbFactory, _paths, _clock, _tmdbTester, _aiResolver, _aiTester, _protector, NullLogger<HealthCheckService>.Instance);

    private void StubTmdbOk()
    {
        _tmdbTester.TestAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbConnectivityTestResult(true, 200, 100, null));
    }

    private void StubAiOff()
    {
        _aiResolver.ResolveOrderedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AiProviderResolution>());
    }

    private void SeedTmdbSetting()
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        TmdbSetting s = db.TmdbSettings.FirstOrDefault() ?? new TmdbSetting();
        s.ApiKeyEncrypted = "cipher-blob";
        if (s.Id == 0) db.TmdbSettings.Add(s);
        db.SaveChanges();
    }

    private void SeedBackupEnabled(bool enabled)
    {
        // EnsureCreated 已种子 Backup_Enabled（默认 true），故 upsert 而非 Add（避免 UNIQUE 冲突）
        using PmmDbContext db = _dbFactory.CreateDbContext();
        SystemSetting? s = db.SystemSettings.FirstOrDefault(x => x.Key == "Backup_Enabled");
        string v = enabled ? "true" : "false";
        if (s is null) db.SystemSettings.Add(new SystemSetting { Key = "Backup_Enabled", Value = v });
        else s.Value = v;
        db.SaveChanges();
    }

    private void SeedNetworkShare(string path, DateTimeOffset? lastReachableAt)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.WatchFolders.Add(new WatchFolder { Path = path, IsNetworkShare = true, Enabled = true, LastReachableAt = lastReachableAt });
        db.SaveChanges();
    }

    private void CreateBackupFile(string name, DateTimeOffset writeTimeUtc)
    {
        Directory.CreateDirectory(_paths.BackupDir);
        string p = Path.Combine(_paths.BackupDir, name);
        File.WriteAllBytes(p, new byte[16]);
        File.SetLastWriteTimeUtc(p, writeTimeUtc.UtcDateTime);
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
