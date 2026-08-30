using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证真实 Migrate() 路径下合并版 Init 迁移的种子完整性</summary>
/// <remarks>
/// 2026-06-11 历史 29 个 migration 合并为单一 Init 后的守护测试：
/// InitMigrationTests 走 EnsureCreated 只覆盖模型 HasData，本类走 Database.Migrate()
/// 额外覆盖移植自 BackfillParseRuleDefaults V1-V3 的 23 条手写 SQL 种子——
/// 这些规则不在模型 HasData 里，EnsureCreated 建出来的库没有，只有迁移链能写入；
/// 若未来再次合并迁移而漏掉移植，本类立刻红灯。
/// </remarks>
public sealed class InitMigrationSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PmmDbContext _ctx;

    public InitMigrationSeedTests()
    {
        // 独立内存库 + 真实迁移链（不复用 EnsureCreated 的 fixture）
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        DbContextOptionsBuilder<PmmDbContext> opts = new();
        opts.UseSqlite(_connection);
        _ctx = new PmmDbContext(opts.Options);
        _ctx.Database.Migrate();
    }

    [Fact]
    public async Task Migrate_SeedsExactly23ParseRuleDefaults()
    {
        List<ParseRule> rules = await _ctx.ParseRules.AsNoTracking().ToListAsync();

        // 23 = 第一批 8 条 + 第二批 7 条 + 第三批 8 条（原 Backfill V1-V3 全量移植）
        rules.Should().HaveCount(23);

        // 三个批次各抽一条代表核对存在性
        rules.Should().Contain(r => r.Name == "国产剧第N季第N集");
        rules.Should().Contain(r => r.Name == "全N集 整季合集");
        rules.Should().Contain(r => r.Name == "综艺 YYMMDD 短日期作集");

        // 字段语义抽查：唯一 ForceType 规则 + 唯一 DefaultType=NULL 的电影规则 + 全部默认启用
        rules.Where(r => r.ForceType).Should().ContainSingle(r => r.Name == "全N集 整季合集");
        rules.Where(r => r.DefaultType == null).Should().ContainSingle(r => r.Name == "括号年份电影");
        rules.Should().OnlyContain(r => r.Enabled);
    }

    [Fact]
    public async Task Migrate_SeedsModelHasDataSameAsEnsureCreated()
    {
        // HasData 五组种子在迁移路径下同样齐全（与 InitMigrationTests 的 EnsureCreated 断言对齐）
        // 37 = 历史 33 + 归档命名模板 4（migration AddArchiveNamingTemplates InsertData）
        (await _ctx.SystemSettings.AsNoTracking().CountAsync()).Should().Be(37);
        (await _ctx.WatchIgnoreRules.AsNoTracking().CountAsync()).Should().Be(16);
        (await _ctx.SystemMediaExtensions.AsNoTracking().CountAsync()).Should().Be(14);
        (await _ctx.TmdbSettings.AsNoTracking().CountAsync()).Should().Be(1);
        (await _ctx.SubtitleSettings.AsNoTracking().CountAsync()).Should().Be(1);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _connection.Close();
        _connection.Dispose();
    }
}
