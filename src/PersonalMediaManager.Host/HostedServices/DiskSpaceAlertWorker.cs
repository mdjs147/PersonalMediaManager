using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>DiskSpaceAlertWorker — 周期巡检归档盘剩余百分比，跌破阈值发 disk.low 告警</summary>
/// <remarks>
/// 补齐「磁盘缓慢填满但无归档活动时永无预警」的缺口：
///   此前 disk.low 全仓唯一触发点是归档动作里的绝对 MB 检查（ArchiveService.EnsureEnoughDiskSpaceAsync），
///   百分比阈值（Archive_DiskWarnPercent / Archive_DiskCriticalPercent）只影响健康检查端点的仪表盘颜色——
///   被动端点不被调用就永远不会通知。本 Worker 按 5 分钟周期主动巡检，把百分比阈值接上 disk.low 通知。
///
/// 巡检口径与 HealthCheckService.CheckDiskAsync 对齐：
///   枚举全部分类（Category_Definition.TargetRoot）所在盘根去重 → 读剩余百分比 →
///   低于 critical 发严重告警、否则低于 warn 发警告（critical 优先，单盘单轮最多一条）；
///   阈值缺失 / 非法回退默认（warn=10 / critical=5）并钳到 [0,100]；0 = 该档检查关闭。
///
/// 告警复用 IAlertService（抑制窗口 System_AlertSuppressMinutes），抑制键与 ArchiveService 同口径
/// 「disk.low:{盘根}」——归档触发与周期巡检共享同一抑制窗口，同盘根因在窗口内只发一条，不重复轰炸
/// （代价：窗口内 warn → critical 升级也被抑制，可接受，窗口默认 60 分钟）。
///
/// 盘容量读取经构造器注入的探测函数（生产默认 DriveInfo）；网络盘 / UNC / 未就绪盘读不到容量 → 跳过不告警
/// （容错口径同健康检查）。单盘异常吞咽不阻断其余盘；单轮异常吞咽不阻断下一轮。
/// </remarks>
public sealed class DiskSpaceAlertWorker : BackgroundService
{
    /// <summary>巡检周期：5 分钟（磁盘填满是慢过程，无需更密）</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(5);

    /// <summary>阈值配置 key（与 HealthCheckService / SystemSettingConfig 种子必须 1:1 对齐）</summary>
    private const string DiskWarnPercentKey = "Archive_DiskWarnPercent";
    private const string DiskCriticalPercentKey = "Archive_DiskCriticalPercent";
    private const int DefaultWarnPercent = 10;
    private const int DefaultCriticalPercent = 5;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IAlertService _alert;
    private readonly ILogger<DiskSpaceAlertWorker> _logger;
    private readonly TimeSpan _period;
    private readonly Func<string, (long TotalBytes, long AvailableBytes)?> _probe;

    public DiskSpaceAlertWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IAlertService alert,
        ILogger<DiskSpaceAlertWorker> logger)
        : this(dbFactory, alert, logger, DefaultPeriod, ProbeDrive) { }

    /// <summary>测试构造器：自定义周期 + 盘容量探测函数（隔离真实 DriveInfo，返 null 表示该盘不可读）</summary>
    internal DiskSpaceAlertWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IAlertService alert,
        ILogger<DiskSpaceAlertWorker> logger,
        TimeSpan period,
        Func<string, (long TotalBytes, long AvailableBytes)?> probe)
    {
        _dbFactory = dbFactory;
        _alert = alert;
        _logger = logger;
        _period = period;
        _probe = probe;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DiskSpaceAlertWorker 启动：周期={Period}", _period);

        using PeriodicTimer timer = new(_period);
        // 启动即先巡一次，避免首轮等 5 分钟
        await SweepSafelyAsync(stoppingToken);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关停
        }

        _logger.LogInformation("DiskSpaceAlertWorker 退出");
    }

    private async Task SweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            await SweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "归档盘空间巡检批次失败（下次周期继续）");
        }
    }

    /// <summary>internal 暴露给测试直连（避免依赖 PeriodicTimer 时序）；返回本轮成功读到容量的盘根数</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        int warnPct;
        int critPct;
        List<string> roots;
        await using (PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct))
        {
            Dictionary<string, string?> cfg = await db.SystemSettings.AsNoTracking()
                .Where(s => s.Key == DiskWarnPercentKey || s.Key == DiskCriticalPercentKey)
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            warnPct = ParsePercentOr(cfg.GetValueOrDefault(DiskWarnPercentKey), DefaultWarnPercent);
            critPct = ParsePercentOr(cfg.GetValueOrDefault(DiskCriticalPercentKey), DefaultCriticalPercent);

            roots = await db.CategoryDefinitions.AsNoTracking()
                .Select(c => c.TargetRoot)
                .ToListAsync(ct);
        }

        if (warnPct == 0 && critPct == 0)
        {
            return 0; // 双阈值均关闭：无需读盘
        }

        List<string> driveRoots = roots
            .Select(TryDriveRoot)
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int probed = 0;
        foreach (string root in driveRoots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                (long TotalBytes, long AvailableBytes)? space = _probe(root);
                if (space is null || space.Value.TotalBytes <= 0)
                {
                    continue; // 网络盘 / 未就绪：读不到容量跳过（容错口径与健康检查一致）
                }
                probed++;

                double freePct = space.Value.AvailableBytes * 100.0 / space.Value.TotalBytes;
                // critical 优先：同盘同轮最多发一条；抑制键与 ArchiveService 绝对 MB 检查同口径（disk.low:{盘根}）
                if (critPct > 0 && freePct < critPct)
                {
                    await RaiseAsync(root, freePct, space.Value.AvailableBytes, critPct, "critical", ct);
                }
                else if (warnPct > 0 && freePct < warnPct)
                {
                    await RaiseAsync(root, freePct, space.Value.AvailableBytes, warnPct, "warn", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "归档盘空间巡检单盘失败（跳过）：Root={Root}", root);
            }
        }
        return probed;
    }

    /// <summary>发出 disk.low 告警（IAlertService 自带抑制窗口与异常吞咽）</summary>
    private async Task RaiseAsync(string root, double freePct, long availableBytes, int thresholdPct, string severity, CancellationToken ct)
    {
        _logger.LogWarning(
            "归档盘剩余空间跌破阈值（{Severity}）：Root={Root}, 剩余 {FreePct:0.#}% < {Threshold}%",
            severity, root, freePct, thresholdPct);
        await _alert.RaiseAsync(
            $"{WebhookEvents.DiskLow}:{root}",
            WebhookEvents.DiskLow,
            new
            {
                drive = root,
                freePercent = Math.Round(freePct, 1),
                availableMb = availableBytes / 1024 / 1024,
                thresholdPercent = thresholdPct,
                severity,
            },
            ct);
    }

    /// <summary>生产默认探测：DriveInfo 读总量/可用量；未就绪 / 异常返 null</summary>
    private static (long TotalBytes, long AvailableBytes)? ProbeDrive(string root)
    {
        try
        {
            DriveInfo drive = new(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return null;
            }
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取路径所在盘根（失败返 null，口径同 HealthCheckService.TryDriveRoot）</summary>
    private static string? TryDriveRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)); }
        catch { return null; }
    }

    /// <summary>解析百分比配置：缺失/非法回退默认，再钳到 [0,100]（口径同 HealthCheckService.ParsePercentOr）</summary>
    private static int ParsePercentOr(string? raw, int fallback)
        => Math.Clamp(int.TryParse(raw, out int v) ? v : fallback, 0, 100);
}
