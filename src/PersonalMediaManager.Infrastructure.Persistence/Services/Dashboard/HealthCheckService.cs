using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Dashboard;
using PersonalMediaManager.Application.Services.Dashboard;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Dashboard;

/// <summary>仪表盘健康检查实现（8 项并行 + 各项 3s 独立超时）</summary>
/// <remarks>
/// 并行约束：每项检查内部独立创建 DbContext，不触发「同 DbContext Task.WhenAll」红线（§八）。
/// 超时策略：CancellationTokenSource(3s) + linked outer ct；超时 / 异常归 fail + detail=超时摘要。
/// 顺序固定：Kestrel / SQLite / TMDB / AI 主提供商 / 备份 / 归档磁盘 / 网络盘 / AI 可用性，前端 v-for 渲染（新增项自动出现）。
/// 「AI 主提供商」测主提供商单点连通；「AI 可用性」测是否至少一个已启用提供商未冷却（两者维度不同，互补）。
/// </remarks>
internal sealed class HealthCheckService : IHealthCheckService
{
    private const int PerCheckTimeoutSeconds = 3;
    private const int BackupStaleHours = 36; // 日备 + 容差：超过视为定时备份疑似失败（与前端 backupStale 阈值一致）
    private const string BackupEnabledKey = "Backup_Enabled";
    private const string DiskWarnPercentKey = "Archive_DiskWarnPercent";
    private const string DiskCriticalPercentKey = "Archive_DiskCriticalPercent";
    private const int DiskPercentMin = 0;
    private const int DiskPercentMax = 100;   // 百分比合法上限：钳位防越界值（>100）把磁盘检查配成恒 fail + 告警风暴

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly AppPaths _paths;
    private readonly IClock _clock;
    private readonly ITmdbConnectivityTester _tmdbTester;
    private readonly IAiProviderResolver _aiResolver;
    private readonly IAiProviderTester _aiTester;
    private readonly IProtectedFieldService _protector;
    private readonly ILogger<HealthCheckService> _logger;

    public HealthCheckService(
        IDbContextFactory<PmmDbContext> dbFactory,
        AppPaths paths,
        IClock clock,
        ITmdbConnectivityTester tmdbTester,
        IAiProviderResolver aiResolver,
        IAiProviderTester aiTester,
        IProtectedFieldService protector,
        ILogger<HealthCheckService> logger)
    {
        _dbFactory = dbFactory;
        _paths = paths;
        _clock = clock;
        _tmdbTester = tmdbTester;
        _aiResolver = aiResolver;
        _aiTester = aiTester;
        _protector = protector;
        _logger = logger;
    }

    public async Task<DashboardHealthResponse> CheckAsync(CancellationToken ct = default)
    {
        Task<HealthCheckEntry> kestrel = SafeRunAsync("Kestrel", () => Task.FromResult(CheckKestrel()), ct);
        Task<HealthCheckEntry> sqlite = SafeRunAsync("SQLite (pmm.db)", () => Task.FromResult(CheckSqlite()), ct);
        Task<HealthCheckEntry> tmdb = SafeRunAsync("TMDB", () => CheckTmdbAsync(ct), ct);
        Task<HealthCheckEntry> ai = SafeRunAsync("AI 主提供商", () => CheckAiAsync(ct), ct);
        Task<HealthCheckEntry> backup = SafeRunAsync("备份", () => CheckBackupAsync(ct), ct);
        Task<HealthCheckEntry> disk = SafeRunAsync("归档磁盘", () => CheckDiskAsync(ct), ct);
        Task<HealthCheckEntry> shares = SafeRunAsync("网络盘", () => CheckNetworkSharesAsync(ct), ct);
        Task<HealthCheckEntry> aiAvail = SafeRunAsync("AI 可用性", () => CheckAiAvailabilityAsync(ct), ct);

        HealthCheckEntry[] results = await Task.WhenAll(kestrel, sqlite, tmdb, ai, backup, disk, shares, aiAvail);
        return new DashboardHealthResponse(results);
    }

    /// <summary>统一包裹：3s 超时 + 异常归 fail，绝不向上抛</summary>
    private async Task<HealthCheckEntry> SafeRunAsync(string name, Func<Task<HealthCheckEntry>> body, CancellationToken outerCt)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(TimeSpan.FromSeconds(PerCheckTimeoutSeconds));
        try
        {
            return await body().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            return new HealthCheckEntry(name, "fail", $"超时（{PerCheckTimeoutSeconds}s）", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "健康检查异常：{Name}", name);
            return new HealthCheckEntry(name, "fail", Shorten(ex.Message, 200), null);
        }
    }

    private HealthCheckEntry CheckKestrel()
    {
        TimeSpan uptime = _clock.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        return new HealthCheckEntry("Kestrel", "ok", $"上线 {FormatUptime(uptime)}", null);
    }

    private HealthCheckEntry CheckSqlite()
    {
        string path = _paths.DbFile;
        if (!File.Exists(path))
        {
            return new HealthCheckEntry("SQLite (pmm.db)", "fail", "pmm.db 不存在", null);
        }
        long bytes = new FileInfo(path).Length;
        return new HealthCheckEntry("SQLite (pmm.db)", "ok", FormatBytes(bytes), null);
    }

    private async Task<HealthCheckEntry> CheckTmdbAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        TmdbSetting? setting = await db.TmdbSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (setting is null || string.IsNullOrEmpty(setting.ApiKeyEncrypted))
        {
            return new HealthCheckEntry("TMDB", "off", "未配置 ApiKey", null);
        }

        string apiKey = _protector.Unprotect(setting.ApiKeyEncrypted);
        TmdbConnectivityTestResult result = await _tmdbTester.TestAsync(apiKey, timeoutSeconds: PerCheckTimeoutSeconds, ct);

        int latency = (int)Math.Round(result.ElapsedMilliseconds);
        if (result.Success)
        {
            return new HealthCheckEntry("TMDB", "ok", $"{result.HttpStatus} OK · {latency}ms", latency);
        }
        return new HealthCheckEntry("TMDB", "fail", Shorten(result.ErrorMessage ?? "失败", 200), latency);
    }

    private async Task<HealthCheckEntry> CheckAiAsync(CancellationToken ct)
    {
        IReadOnlyList<AiProviderResolution> ordered = await _aiResolver.ResolveOrderedAsync(ct);
        AiProviderResolution? primary = ordered.FirstOrDefault(p => p.IsPrimary) ?? ordered.FirstOrDefault();
        if (primary is null)
        {
            return new HealthCheckEntry("AI 主提供商", "off", "未配置可用 Provider", null);
        }

        AiProviderTestResult result = await _aiTester.TestAsync(
            primary.Type, primary.Endpoint.BaseUrl, primary.Endpoint.ApiKey, primary.Endpoint.Model,
            timeoutSeconds: PerCheckTimeoutSeconds, useProxy: primary.Endpoint.UseProxy, ct);

        int latency = (int)Math.Round(result.ElapsedMilliseconds);
        if (result.Success)
        {
            return new HealthCheckEntry("AI 主提供商", "ok", $"{primary.Name} · {latency}ms", latency);
        }
        return new HealthCheckEntry("AI 主提供商", "fail", Shorten($"{primary.Name} · {result.ErrorMessage ?? "失败"}", 200), latency);
    }

    /// <summary>备份健康：未启用→off；启用但久未备份（超 36h）或无产物→fail（红，疑似定时失败）；新鲜→ok</summary>
    private async Task<HealthCheckEntry> CheckBackupAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == BackupEnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        bool enabled = string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            return new HealthCheckEntry("备份", "off", "未启用自动备份", null);
        }

        DateTimeOffset? last = GetLastBackupTime();
        if (last is null)
        {
            return new HealthCheckEntry("备份", "fail", "已启用但尚无备份产物", null);
        }

        TimeSpan age = _clock.UtcNow - last.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        string ago = $"最近备份 {FormatUptime(age)}前";
        return age > TimeSpan.FromHours(BackupStaleHours)
            ? new HealthCheckEntry("备份", "fail", $"{ago}（疑似失败，请查任务历史）", null)
            : new HealthCheckEntry("备份", "ok", ago, null);
    }

    /// <summary>最近备份时间 = backups/ 内最新 pmm-backup-*.zip 的文件写入时间；无产物返 null</summary>
    private DateTimeOffset? GetLastBackupTime()
    {
        try
        {
            if (!Directory.Exists(_paths.BackupDir)) return null;
            string? newest = Directory.EnumerateFiles(_paths.BackupDir, "pmm-backup-*.zip")
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(newest), TimeSpan.Zero);
        }
        catch { return null; }
    }

    /// <summary>归档磁盘：枚举所有分类目标根所在盘，取剩余百分比最低者；低于 critical→fail、低于 warn→warn</summary>
    private async Task<HealthCheckEntry> CheckDiskAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        Dictionary<string, string?> cfg = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == DiskWarnPercentKey || s.Key == DiskCriticalPercentKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        int warnPct = ParsePercentOr(cfg.GetValueOrDefault(DiskWarnPercentKey), 10);
        int critPct = ParsePercentOr(cfg.GetValueOrDefault(DiskCriticalPercentKey), 5);

        List<string> roots = await db.CategoryDefinitions.AsNoTracking()
            .Select(c => c.TargetRoot)
            .ToListAsync(ct);
        List<string> driveRoots = roots
            .Select(TryDriveRoot)
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (driveRoots.Count == 0)
            return new HealthCheckEntry("归档磁盘", "off", "未配置归档分类", null);

        double worstPct = double.MaxValue;
        string? detail = null;
        foreach (string root in driveRoots)
        {
            try
            {
                DriveInfo drive = new(root);
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                double freePct = drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
                if (freePct < worstPct)
                {
                    worstPct = freePct;
                    detail = $"{root} 剩余 {freePct:0.#}%（{FormatBytes(drive.AvailableFreeSpace)}）";
                }
            }
            catch { /* 单盘读取失败跳过，不阻断其余盘 */ }
        }
        if (detail is null)
            return new HealthCheckEntry("归档磁盘", "off", "归档盘不可读（网络盘 / 未就绪）", null);

        string status = critPct > 0 && worstPct < critPct ? "fail"
            : warnPct > 0 && worstPct < warnPct ? "warn"
            : "ok";
        return new HealthCheckEntry("归档磁盘", status, detail, null);
    }

    /// <summary>网络盘：启用的网络共享监控目录是否 2 分钟内可达；有不可达→warn，无网络盘→off</summary>
    private async Task<HealthCheckEntry> CheckNetworkSharesAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        DateTimeOffset threshold = _clock.UtcNow.AddMinutes(-2);
        var shares = await db.WatchFolders.AsNoTracking()
            .Where(w => w.IsNetworkShare && w.Enabled)
            .Select(w => new { w.Path, w.LastReachableAt })
            .ToListAsync(ct);
        if (shares.Count == 0)
            return new HealthCheckEntry("网络盘", "off", "无网络共享监控目录", null);

        List<string> unreachable = shares
            .Where(s => s.LastReachableAt is null || s.LastReachableAt < threshold)
            .Select(s => s.Path)
            .ToList();
        if (unreachable.Count == 0)
            return new HealthCheckEntry("网络盘", "ok", $"{shares.Count} 个网络盘全部可达", null);
        return new HealthCheckEntry("网络盘", "warn",
            $"{unreachable.Count}/{shares.Count} 个不可达：{string.Join("、", unreachable.Take(3))}", null);
    }

    /// <summary>AI 可用性：已启用提供商是否至少一个未冷却可用；全冷却→fail，无启用→off（区别于「AI 主提供商」单点 ping）</summary>
    private async Task<HealthCheckEntry> CheckAiAvailabilityAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        bool anyEnabled = await db.ParseAiProviders.AsNoTracking().AnyAsync(p => p.Enabled, ct);
        if (!anyEnabled)
            return new HealthCheckEntry("AI 可用性", "off", "未启用任何 AI 提供商", null);

        IReadOnlyList<AiProviderResolution> ordered = await _aiResolver.ResolveOrderedAsync(ct);
        if (ordered.Count == 0)
            return new HealthCheckEntry("AI 可用性", "fail", "已启用的 AI 提供商全部不可用（冷却中或配置失效）", null);
        return new HealthCheckEntry("AI 可用性", "ok", $"{ordered.Count} 个提供商可用", null);
    }

    /// <summary>取路径所在盘根（失败返 null）</summary>
    private static string? TryDriveRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)); }
        catch { return null; }
    }

    /// <summary>解析百分比配置：缺失/非法回退默认，再钳到 [0,100] 兜底（防写侧绕过的越界值把磁盘检查配成恒 fail）</summary>
    internal static int ParsePercentOr(string? raw, int fallback)
        => Math.Clamp(int.TryParse(raw, out int v) ? v : fallback, DiskPercentMin, DiskPercentMax);

    private static string FormatUptime(TimeSpan u)
    {
        if (u.TotalDays >= 1) return $"{(int)u.TotalDays}d {u.Hours}h";
        if (u.TotalHours >= 1) return $"{(int)u.TotalHours}h {u.Minutes}m";
        if (u.TotalMinutes >= 1) return $"{(int)u.TotalMinutes}m {u.Seconds}s";
        return $"{(int)u.TotalSeconds}s";
    }

    private static string FormatBytes(long b)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (b >= GB) return $"{b / (double)GB:0.##} GB";
        if (b >= MB) return $"{b / (double)MB:0.#} MB";
        if (b >= KB) return $"{b / (double)KB:0.#} KB";
        return $"{b} B";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max];
}
