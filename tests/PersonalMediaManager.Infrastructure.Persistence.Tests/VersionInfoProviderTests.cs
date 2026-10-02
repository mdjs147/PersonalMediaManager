using System.Data;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Infrastructure.Persistence.Services.Versioning;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>统一产品版本与只读 EF 迁移诊断回归</summary>
public sealed class VersionInfoProviderTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContextFactory _factory;
    private readonly string[] _migrations;

    public VersionInfoProviderTests()
    {
        _factory = new TestDbContextFactory(_connection);
        using PmmDbContext db = _factory.CreateDbContext();
        _migrations = db.Database.GetMigrations().ToArray();
    }

    public void Dispose() => _connection.Dispose();

    [Fact(DisplayName = "版本兼容字段只读主版本，旧前端和数据库元数据不再控制结果")]
    public void StaticMetadata_UsesOneProductVersionAndSeparateDiagnostics()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"PmmVersionTest_{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        ConstructorInfo metadataConstructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["ProductVersion", "0.4.0"]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["FrontendVersion", "8.7.6"]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["DbVersion", "9.9.9"]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["BuildTimeUtc", "2026-10-02T12:34:56Z"]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            ["7.8.9+1234abcd.dirty"]));

        StaticVersionInfo info = VersionInfoProvider.LoadStaticVersionInfo(assembly, _migrations[^1]);

        info.Product.Should().Be("0.4.0");
        info.Backend.Should().Be(info.Product);
        info.Frontend.Should().Be(info.Product);
        info.DbVersionTarget.Should().Be(_migrations[^1]);
        info.Commit.Should().Be("1234abcd");
        info.Dirty.Should().BeTrue();
        info.BuildTime.Should().Be(new DateTimeOffset(2026, 10, 2, 12, 34, 56, TimeSpan.Zero));
    }

    [Fact(DisplayName = "静态信息无需开库，测试宿主缺少产品元数据时回退产品程序集")]
    public void GetStatic_DoesNotOpenDatabaseAndUsesProductAssemblyMetadata()
    {
        StaticVersionInfo info = NewProvider().GetStatic();
        string product = typeof(VersionInfoProvider).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ProductVersion").Value!;

        _connection.State.Should().Be(ConnectionState.Closed);
        info.Product.Should().Be(product).And.NotBe("0.0.0");
        info.Backend.Should().Be(product);
        info.Frontend.Should().Be(product);
        info.DbVersionTarget.Should().Be(_migrations[^1]);
    }

    [Fact(DisplayName = "尚无历史表时所有迁移待应用，版本查询不建表或执行迁移")]
    public async Task MissingHistoryTable_ReturnsAllPendingWithoutCreatingTables()
    {
        _connection.Open();

        DbVersionStatus status = (await NewProvider().GetFullAsync()).Database;

        status.HistoryAvailable.Should().BeTrue();
        status.Status.Should().Be("pending");
        status.NeedsMigration.Should().BeTrue();
        status.Applied.Should().Be("unknown");
        status.AppliedMigrationId.Should().BeNull();
        status.PendingMigrationIds.Should().Equal(_migrations);
        status.UnknownMigrationIds.Should().BeEmpty();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
        Convert.ToInt64(command.ExecuteScalar()).Should().Be(0, "版本查询只能读取，不能创建历史表或应用迁移");
    }

    [Fact(DisplayName = "完整已应用集合视为对齐，保留历史表中的 EF 工具版本")]
    public async Task CompleteHistory_IsUpToDateAndRemainsUnchanged()
    {
        CreateHistory(_migrations);

        VersionInfoResponse info = await NewProvider().GetFullAsync();

        info.Backend.Should().Be(info.Product);
        info.Frontend.Should().Be(info.Product);
        info.Database.Target.Should().Be(_migrations[^1]);
        info.Database.Applied.Should().Be(_migrations[^1]);
        info.Database.AppliedMigrationId.Should().Be(_migrations[^1]);
        info.Database.HistoryAvailable.Should().BeTrue();
        info.Database.Status.Should().Be("upToDate");
        info.Database.NeedsMigration.Should().BeFalse();
        info.Database.PendingMigrationIds.Should().BeEmpty();
        info.Database.UnknownMigrationIds.Should().BeEmpty();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE ProductVersion = '10.0.8'";
        Convert.ToInt64(command.ExecuteScalar()).Should().Be(_migrations.Length);
    }

    [Fact(DisplayName = "正常历史前缀返回待应用迁移")]
    public async Task OlderPrefix_IsPending()
    {
        CreateHistory(_migrations[..^1]);

        DbVersionStatus status = (await NewProvider().GetFullAsync()).Database;

        status.Status.Should().Be("pending");
        status.NeedsMigration.Should().BeTrue();
        status.PendingMigrationIds.Should().Equal(_migrations[^1]);
        status.UnknownMigrationIds.Should().BeEmpty();
    }

    [Fact(DisplayName = "最大迁移已到目标但缺中间记录时不能误报对齐")]
    public async Task MissingIntermediateMigration_IsIncompatibleAndPending()
    {
        _migrations.Length.Should().BeGreaterThan(2);
        CreateHistory(_migrations.Where(id => id != _migrations[1]));

        DbVersionStatus status = (await NewProvider().GetFullAsync()).Database;

        status.AppliedMigrationId.Should().Be(status.Target);
        status.Status.Should().Be("incompatible");
        status.NeedsMigration.Should().BeTrue();
        status.PendingMigrationIds.Should().Equal(_migrations[1]);
        status.UnknownMigrationIds.Should().BeEmpty();
    }

    [Theory(DisplayName = "新于或旧于代码目标的未知迁移都不能误报对齐")]
    [InlineData("99991231235959_NewerUnknown")]
    [InlineData("20000101000000_OlderUnknown")]
    public async Task UnknownAppliedMigration_IsIncompatible(string unknown)
    {
        CreateHistory(_migrations.Append(unknown));

        DbVersionStatus status = (await NewProvider().GetFullAsync()).Database;

        status.Status.Should().Be("incompatible");
        status.NeedsMigration.Should().BeFalse();
        status.PendingMigrationIds.Should().BeEmpty();
        status.UnknownMigrationIds.Should().Equal(unknown);
    }

    [Fact(DisplayName = "更新的未知迁移不能掩盖当前代码中的待应用迁移")]
    public async Task NewerUnknownAndMissingKnownMigration_ReportsBoth()
    {
        const string unknown = "99991231235959_NewerUnknown";
        CreateHistory(_migrations[..^1].Append(unknown));

        DbVersionStatus status = (await NewProvider().GetFullAsync()).Database;

        status.Status.Should().Be("incompatible");
        status.NeedsMigration.Should().BeTrue();
        status.PendingMigrationIds.Should().Equal(_migrations[^1]);
        status.UnknownMigrationIds.Should().Equal(unknown);
    }

    [Fact(DisplayName = "迁移历史损坏不可读时明确未知，不能误报已对齐")]
    public async Task UnreadableHistory_IsUnknown()
    {
        _connection.Open();
        using SqliteCommand command = _connection.CreateCommand();
        // 故意构造不满足 EF 历史表约束的记录，验证读取异常不会降级为已对齐。
        command.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT, ProductVersion TEXT); INSERT INTO __EFMigrationsHistory VALUES (NULL, NULL)";
        command.ExecuteNonQuery();

        DbVersionStatus status = (await NewProvider().GetFullAsync()).Database;

        status.Status.Should().Be("unknown");
        status.HistoryAvailable.Should().BeFalse();
        status.Applied.Should().Be("unknown");
        status.AppliedMigrationId.Should().BeNull();
        status.PendingMigrationIds.Should().BeEmpty("读取失败时不能伪造已知缺失迁移");
    }

    [Fact(DisplayName = "取消迁移诊断查询时传播取消")]
    public async Task CancelledQuery_PropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Func<Task> query = () => NewProvider().GetFullAsync(cts.Token);

        await query.Should().ThrowAsync<OperationCanceledException>();
    }

    private VersionInfoProvider NewProvider() => new(_factory, NullLogger<VersionInfoProvider>.Instance);

    private void CreateHistory(IEnumerable<string> migrationIds)
    {
        _connection.Open();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, '10.0.8')";
        SqliteParameter idParameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (string id in migrationIds)
        {
            idParameter.Value = id;
            command.ExecuteNonQuery();
        }
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        public PmmDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection).Options);
    }
}
