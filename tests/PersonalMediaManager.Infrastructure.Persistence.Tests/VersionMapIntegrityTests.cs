using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>守护 version-map.json 与实际 EF migration / 构建期 PmmDbVersion 的一致性</summary>
/// <remarks>
/// 本套测试是该映射表唯一的回归网：历史上曾连续多次新增 migration 漏登记，导致 floor 反查 applied 版本停在旧值、
/// 且 target 落到未登记版本时 needsMigration 被强制 false（升级检查失灵）。
/// 不依赖已迁移的 __EFMigrationsHistory（fixture 走 EnsureCreated，无历史表），全部基于确定性来源：
///   1. 嵌入资源 version-map.json（与运行时 VersionInfoProvider 读同一份）
///   2. DbContext.Database.GetMigrations()（编译进程序集的全部 migration，按 migrationId 升序）
///   3. Infrastructure.Persistence 程序集 AssemblyMetadata["DbVersion"]（= 构建期 PmmDbVersion target）
/// </remarks>
public sealed class VersionMapIntegrityTests : IClassFixture<PmmDbContextTestFixture>
{
    private const string ResourceName = "PersonalMediaManager.Infrastructure.Persistence.version-map.json";

    private readonly PmmDbContextTestFixture _fixture;

    public VersionMapIntegrityTests(PmmDbContextTestFixture fixture) => _fixture = fixture;

    [Fact(DisplayName = "version-map 每个 migrationId 都对应真实存在的 EF migration")]
    public void EveryAnchor_IsRealMigration()
    {
        IReadOnlyList<MapEntry> map = LoadMap();
        HashSet<string> all = GetAllMigrations().ToHashSet(StringComparer.Ordinal);

        foreach (MapEntry e in map)
        {
            all.Should().Contain(e.MigrationId,
                $"version-map 行 db={e.Db} 的 migrationId 必须对应真实 migration（拼写/改名/删除会让 floor 反查与 needsMigration 失准）");
        }
    }

    [Fact(DisplayName = "最新 migration 已登记为最高锚点（防新增 migration 漏更新映射）")]
    public void LatestMigration_IsRegisteredAsTopAnchor()
    {
        IReadOnlyList<MapEntry> map = LoadMap();
        string latestMigration = GetAllMigrations().Last();
        string topAnchor = map.OrderBy(e => e.MigrationId, StringComparer.Ordinal).Last().MigrationId;

        topAnchor.Should().Be(latestMigration,
            "version-map 中时间戳最大的锚点必须等于程序集里最新的 migration；不等说明新增 migration 后忘了往 version-map 追加行——正是本测试要拦截的历史故障");
    }

    [Fact(DisplayName = "目标 PmmDbVersion 在 version-map 可解析（否则 needsMigration 恒 false）")]
    public void TargetDbVersion_IsResolvableAndMatchesTopAnchor()
    {
        string target = GetAssemblyDbVersionTarget();
        IReadOnlyList<MapEntry> map = LoadMap();

        string topDb = map.OrderBy(e => e.MigrationId, StringComparer.Ordinal).Last().Db;
        topDb.Should().Be(target,
            $"最新 migration 对应的 db 版本（{topDb}）必须等于程序集注入的 PmmDbVersion target（{target}）；不等会让 /system/version 的 applied 与 target 永久错位");

        map.Should().Contain(e => e.Db == target,
            $"PmmDbVersion target={target} 必须能在 version-map 找到对应行——VersionInfoProvider.ResolveMigrationIdForVersion 找不到时 needsMigration 会被强制 false（升级检查彻底失灵）");
    }

    [Fact(DisplayName = "锚点 db 版本两两不重复（一版本一行）")]
    public void AnchorDbVersions_AreUnique()
    {
        IReadOnlyList<MapEntry> map = LoadMap();
        map.Select(e => e.Db).Should().OnlyHaveUniqueItems(
            "每个 db 版本只应有一行锚点；重复会让 ResolveMigrationIdForVersion 取首行产生歧义");
    }

    // --- helpers ---

    private static IReadOnlyList<MapEntry> LoadMap()
    {
        using Stream? s = typeof(PmmDbContext).Assembly.GetManifestResourceStream(ResourceName);
        s.Should().NotBeNull($"version-map.json 必须以资源名 {ResourceName} 嵌入 Infrastructure.Persistence");
        using JsonDocument doc = JsonDocument.Parse(s!);
        List<MapEntry> list = [];
        foreach (JsonElement v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            list.Add(new MapEntry(v.GetProperty("db").GetString()!, v.GetProperty("migrationId").GetString()!));
        }
        list.Should().NotBeEmpty();
        return list;
    }

    private IReadOnlyList<string> GetAllMigrations()
    {
        using PmmDbContext ctx = _fixture.CreateContext();
        return ctx.Database.GetMigrations().ToList();
    }

    private static string GetAssemblyDbVersionTarget()
    {
        string? v = typeof(PmmDbContext).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DbVersion")?.Value;
        v.Should().NotBeNullOrWhiteSpace("Infrastructure.Persistence 程序集应由 InjectVersionMetadata 注入 DbVersion 元数据");
        return v!;
    }

    private sealed record MapEntry(string Db, string MigrationId);
}
