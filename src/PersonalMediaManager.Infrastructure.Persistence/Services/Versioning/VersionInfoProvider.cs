using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Versioning;

/// <summary>IVersionInfoProvider 实现：反射 assembly metadata + 查 __EFMigrationsHistory + 解析嵌入的 version-map.json</summary>
/// <remarks>
/// 单例：静态信息（assembly metadata）+ 嵌入资源（version-map.json）在构造时一次性加载，后续只查 db。
/// 启动期把版本号自检结果打到 ILogger（needsMigration 时升 Warning，配合 Host 启动 banner 一并暴露）。
/// </remarks>
public sealed class VersionInfoProvider : IVersionInfoProvider
{
    private const string VersionMapResourceName = "PersonalMediaManager.Infrastructure.Persistence.version-map.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IDbContextFactory<PmmDbContext> _factory;
    private readonly ILogger<VersionInfoProvider> _logger;
    private readonly StaticVersionInfo _static;
    private readonly VersionMapData _versionMap;

    public VersionInfoProvider(IDbContextFactory<PmmDbContext> factory, ILogger<VersionInfoProvider> logger)
    {
        _factory = factory;
        _logger = logger;
        _static = LoadStaticVersionInfo();
        _versionMap = LoadVersionMap(_logger);
    }

    public StaticVersionInfo GetStatic() => _static;

    public async Task<VersionInfoResponse> GetFullAsync(CancellationToken ct = default)
    {
        string? appliedMigrationId = await GetAppliedMigrationIdAsync(ct).ConfigureAwait(false);
        string applied = ResolveAppliedDbVersion(appliedMigrationId);
        string target = _static.DbVersionTarget;
        string? targetMigrationId = ResolveMigrationIdForVersion(target);

        // applied 与 target migrationId 都是时间戳前缀，字典序 = 时间序
        // applied < target → 数据库 schema 落后，需要 EF Migrate
        bool needsMigration = targetMigrationId is not null
            && (appliedMigrationId is null
                || string.Compare(appliedMigrationId, targetMigrationId, StringComparison.Ordinal) < 0);

        return new VersionInfoResponse
        {
            Product = _static.Product,
            Backend = _static.Backend,
            Frontend = _static.Frontend,
            Database = new DbVersionStatus
            {
                Target = target,
                Applied = applied,
                AppliedMigrationId = appliedMigrationId,
                NeedsMigration = needsMigration,
            },
            Commit = _static.Commit,
            Dirty = _static.Dirty,
            BuildTime = _static.BuildTime,
            Framework = _static.Framework,
        };
    }

    /// <summary>查 __EFMigrationsHistory 最大 MigrationId；db 未就绪 / 表不存在时返 null（首启场景）</summary>
    private async Task<string?> GetAppliedMigrationIdAsync(CancellationToken ct)
    {
        try
        {
            await using PmmDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            System.Data.Common.DbConnection conn = db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using System.Data.Common.DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result as string;
        }
        catch (Exception ex)
        {
            // 首启 db 文件刚建、__EFMigrationsHistory 未生成；不当成错误，仅 Debug 留痕
            _logger.LogDebug(ex, "读取 __EFMigrationsHistory 失败（首启或 db 不可达），applied 版本号置为 unknown");
            return null;
        }
    }

    /// <summary>查 version-map.json：找 migrationId ≤ applied 的最大版本</summary>
    private string ResolveAppliedDbVersion(string? appliedMigrationId)
    {
        if (string.IsNullOrEmpty(appliedMigrationId) || _versionMap.Versions.Count == 0)
        {
            return "unknown";
        }

        // 时间戳前缀的字典序 = 时间序；倒序遍历找第一个 ≤ applied
        foreach (VersionMapEntry entry in _versionMap.Versions
            .OrderByDescending(v => v.MigrationId, StringComparer.Ordinal))
        {
            if (string.Compare(appliedMigrationId, entry.MigrationId, StringComparison.Ordinal) >= 0)
            {
                return entry.Db;
            }
        }

        return "unknown";
    }

    private string? ResolveMigrationIdForVersion(string dbVersion)
    {
        return _versionMap.Versions.FirstOrDefault(v => v.Db == dbVersion)?.MigrationId;
    }

    private static StaticVersionInfo LoadStaticVersionInfo()
    {
        // entry assembly 在 Launcher 是 PersonalMediaManager（exe），在测试夹具是 testhost；
        // 回退到本程序集保证测试环境不空。所有 dll 的 metadata 在同一次构建产物中一致。
        Assembly asm = Assembly.GetEntryAssembly() ?? typeof(VersionInfoProvider).Assembly;
        Dictionary<string, string> meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => !string.IsNullOrEmpty(a.Key))
            .ToDictionary(a => a.Key!, a => a.Value ?? string.Empty, StringComparer.Ordinal);

        string product = meta.GetValueOrDefault("ProductVersion", "0.0.0");
        string dbTarget = meta.GetValueOrDefault("DbVersion", "0.0.0");
        string frontend = meta.GetValueOrDefault("FrontendVersion", "0.0.0");

        DateTimeOffset? buildTime = null;
        if (meta.TryGetValue("BuildTimeUtc", out string? buildStr)
            && DateTimeOffset.TryParse(buildStr, out DateTimeOffset parsed))
        {
            buildTime = parsed;
        }

        string informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";

        // 从 InformationalVersion 拆 commit 与 dirty 标志：形如 "0.1.0+a1b2c3d4" 或 "0.1.0+a1b2c3d4.dirty"
        string commit = string.Empty;
        bool dirty = false;
        int plus = informational.IndexOf('+');
        if (plus > 0 && plus < informational.Length - 1)
        {
            string rest = informational[(plus + 1)..];
            int dotDirty = rest.IndexOf(".dirty", StringComparison.Ordinal);
            if (dotDirty >= 0)
            {
                commit = rest[..dotDirty];
                dirty = true;
            }
            else
            {
                commit = rest;
            }
        }

        return new StaticVersionInfo
        {
            Product = product,
            Backend = informational,
            Frontend = frontend,
            DbVersionTarget = dbTarget,
            Commit = commit,
            Dirty = dirty,
            BuildTime = buildTime,
            Framework = RuntimeInformation.FrameworkDescription,
        };
    }

    private static VersionMapData LoadVersionMap(ILogger logger)
    {
        try
        {
            Assembly asm = typeof(VersionInfoProvider).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(VersionMapResourceName);
            if (stream is null)
            {
                logger.LogWarning("version-map.json 资源未嵌入（资源名 {Name}），applied 版本号将一律返回 unknown",
                    VersionMapResourceName);
                return VersionMapData.Empty;
            }
            using StreamReader reader = new(stream);
            string content = reader.ReadToEnd();
            VersionMapData? data = JsonSerializer.Deserialize<VersionMapData>(content, JsonOptions);
            return data ?? VersionMapData.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析 version-map.json 失败，applied 版本号将一律返回 unknown");
            return VersionMapData.Empty;
        }
    }

    private sealed class VersionMapData
    {
        public static readonly VersionMapData Empty = new();

        [JsonPropertyName("versions")]
        public List<VersionMapEntry> Versions { get; init; } = [];
    }

    private sealed class VersionMapEntry
    {
        [JsonPropertyName("db")]
        public string Db { get; init; } = string.Empty;

        [JsonPropertyName("migrationId")]
        public string MigrationId { get; init; } = string.Empty;
    }
}
