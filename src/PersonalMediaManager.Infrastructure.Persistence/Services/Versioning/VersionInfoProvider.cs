using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Versioning;

/// <summary>提供统一产品版本和 EF 迁移集合诊断</summary>
/// <remarks>单例缓存程序集元数据与代码迁移标识；动态查询只读历史，不执行迁移。</remarks>
public sealed class VersionInfoProvider : IVersionInfoProvider
{
    private readonly IDbContextFactory<PmmDbContext> _factory;
    private readonly ILogger<VersionInfoProvider> _logger;
    private readonly StaticVersionInfo _static;
    private readonly string[] _knownMigrationIds;

    public VersionInfoProvider(IDbContextFactory<PmmDbContext> factory, ILogger<VersionInfoProvider> logger)
    {
        _factory = factory;
        _logger = logger;
        // GetMigrations 只读取编译后的迁移元数据，不打开数据库连接。
        using PmmDbContext db = factory.CreateDbContext();
        _knownMigrationIds = db.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        _static = LoadStaticVersionInfo(_knownMigrationIds.LastOrDefault() ?? string.Empty);
    }

    public StaticVersionInfo GetStatic() => _static;

    public async Task<VersionInfoResponse> GetFullAsync(CancellationToken ct = default)
    {
        string[]? appliedMigrationIds = await GetAppliedMigrationIdsAsync(ct).ConfigureAwait(false);
        return new VersionInfoResponse
        {
            Product = _static.Product,
            Backend = _static.Product,
            Frontend = _static.Product,
            Database = BuildMigrationStatus(appliedMigrationIds),
            Commit = _static.Commit,
            Dirty = _static.Dirty,
            BuildTime = _static.BuildTime,
            Framework = _static.Framework,
        };
    }

    /// <summary>读取全部 EF 迁移历史；不可读时返回 null，取消请求照常传播</summary>
    private async Task<string[]?> GetAppliedMigrationIdsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using PmmDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false))
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "读取 __EFMigrationsHistory 失败，数据库迁移状态未知");
            return null;
        }
    }

    /// <summary>按完整迁移集合判断缺失、未知迁移和链中间缺口</summary>
    private DbVersionStatus BuildMigrationStatus(string[]? appliedMigrationIds)
    {
        if (appliedMigrationIds is null)
        {
            return new DbVersionStatus { Target = _static.DbVersionTarget, Applied = "unknown" };
        }

        HashSet<string> applied = appliedMigrationIds.ToHashSet(StringComparer.Ordinal);
        string[] pending = _knownMigrationIds.Except(applied, StringComparer.Ordinal).ToArray();
        string[] unknown = appliedMigrationIds.Except(_knownMigrationIds, StringComparer.Ordinal).ToArray();
        string? latestApplied = appliedMigrationIds.LastOrDefault();
        // 正常待升级历史必须是代码迁移链的连续前缀；最大 ID 对齐不能掩盖中间缺口。
        bool hasGap = latestApplied is not null
            && pending.Any(id => string.CompareOrdinal(id, latestApplied) < 0);
        string status = unknown.Length > 0 || hasGap ? "incompatible"
            : pending.Length > 0 ? "pending" : "upToDate";

        return new DbVersionStatus
        {
            Target = _static.DbVersionTarget,
            Applied = latestApplied ?? "unknown",
            AppliedMigrationId = latestApplied,
            NeedsMigration = pending.Length > 0,
            HistoryAvailable = true,
            Status = status,
            PendingMigrationIds = pending,
            UnknownMigrationIds = unknown,
        };
    }

    private static StaticVersionInfo LoadStaticVersionInfo(string dbTarget)
    {
        // testhost 等外部入口没有产品元数据时回退到本程序集，避免读出测试宿主版本。
        Assembly? entryAssembly = Assembly.GetEntryAssembly();
        Assembly asm = entryAssembly is not null && entryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "ProductVersion" && !string.IsNullOrWhiteSpace(a.Value))
            ? entryAssembly : typeof(VersionInfoProvider).Assembly;
        return LoadStaticVersionInfo(asm, dbTarget);
    }

    /// <summary>读取同一程序集中的主版本与构建诊断信息</summary>
    internal static StaticVersionInfo LoadStaticVersionInfo(Assembly asm, string dbTarget)
    {
        Dictionary<string, string> meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => !string.IsNullOrEmpty(a.Key))
            .ToDictionary(a => a.Key!, a => a.Value ?? string.Empty, StringComparer.Ordinal);

        string product = meta.GetValueOrDefault("ProductVersion", "0.0.0");

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
            Backend = product,
            Frontend = product,
            DbVersionTarget = dbTarget,
            Commit = commit,
            Dirty = dirty,
            BuildTime = buildTime,
            Framework = RuntimeInformation.FrameworkDescription,
        };
    }
}
