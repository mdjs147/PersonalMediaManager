using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>启动期 db 自愈逻辑：预 checkpoint + Migrate + 单次重试控制流</summary>
/// <remarks>
/// 用 in-memory SQLite fixture（quick_check 永远返回 ok）+ 注入 migrate 委托，
/// 直接验证 MigrateWithSelfHealCore 在四种典型路径下的行为：
/// 正常 / WAL 误报自愈成功 / 非自愈错码直抛 / 自愈仍失败原样抛。
/// </remarks>
public sealed class DatabaseStartupHealerTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public DatabaseStartupHealerTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void MigrateWithSelfHeal_WhenMigrateSucceeds_CallsOnce()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        int callCount = 0;

        DatabaseStartupHealer.MigrateWithSelfHealCore(
            ctx,
            () => callCount++,
            NullLogger.Instance);

        callCount.Should().Be(1);
    }

    [Fact]
    public void MigrateWithSelfHeal_OnSqliteCorruptAndQuickCheckOk_RetriesOnceAndSucceeds()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        int callCount = 0;

        DatabaseStartupHealer.MigrateWithSelfHealCore(
            ctx,
            () =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // errcode=11 触发自愈分支；in-memory 的 quick_check 必然返回 ok → 进入重试
                    throw new SqliteException("database disk image is malformed", 11);
                }
            },
            NullLogger.Instance);

        callCount.Should().Be(2);
    }

    [Fact]
    public void MigrateWithSelfHeal_OnNonCorruptSqliteError_RethrowsWithoutRetry()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        int callCount = 0;

        Action act = () => DatabaseStartupHealer.MigrateWithSelfHealCore(
            ctx,
            () =>
            {
                callCount++;
                // errcode=5 (SQLITE_BUSY) — 不属于自愈窗口，原样上抛
                throw new SqliteException("database is locked", 5);
            },
            NullLogger.Instance);

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(5);
        callCount.Should().Be(1);
    }

    [Fact]
    public void MigrateWithSelfHeal_OnNonSqliteException_RethrowsWithoutRetry()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        int callCount = 0;

        Action act = () => DatabaseStartupHealer.MigrateWithSelfHealCore(
            ctx,
            () =>
            {
                callCount++;
                throw new InvalidOperationException("迁移配置异常");
            },
            NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("迁移配置异常");
        callCount.Should().Be(1);
    }

    [Fact]
    public void MigrateWithSelfHeal_WhenRetryAlsoThrowsCorrupt_RethrowsAndStopsAtTwoAttempts()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        int callCount = 0;

        Action act = () => DatabaseStartupHealer.MigrateWithSelfHealCore(
            ctx,
            () =>
            {
                callCount++;
                throw new SqliteException("database disk image is malformed", 11);
            },
            NullLogger.Instance);

        // 第一次进 catch → quick_check=ok → 重试；第二次仍抛 → 已不在 try 中，直接传播；总共 2 次
        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(11);
        callCount.Should().Be(2);
    }
}
