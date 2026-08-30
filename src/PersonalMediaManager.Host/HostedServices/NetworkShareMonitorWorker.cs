using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>NetworkShareMonitorWorker（D6.4）— 60s 周期检测 IsNetworkShare=true 监控目录可达性</summary>
/// <remarks>
/// 触达 WatchFolder.MarkReachable / MarkUnreachable（D1.4），按状态扭转产 WatchFolderReachabilityChangedEvent，
/// 并经 IWatchRebuildSignal 通知 FileWatcherWorker 做挂载 / 卸载（需求文档 §3.2 网络盘支持）：
///   - 恢复可达（IsReachable=true）→ ShareRecovered 信号：重建该目录 watcher + 触发一次全量补扫
///     （UNC 上的 FSW 断开后内部已失效，不重建则恢复后监控静默死亡）；
///   - 转不可达（IsReachable=false）→ ShareLost 信号：暂停该目录监控（卸载死 FSW），并主动告警。
///
/// 仅扫描 Enabled=true 且 IsNetworkShare=true 且 AutoScan=true 的目录：本地盘视为永远可达不参与；
/// 禁用 / 关闭自动监控的目录无需关心（后者无 watcher 可重建、无自动补扫，探测纯属浪费）。
///
/// 探测用 Directory.Exists：单次 UNC 死路径可能阻塞 30 秒（SMB 协商超时），
/// 包裹 Task.Run + WaitAsync(5s) 短超时，避免一个不可达共享拖死整批扫描；
/// 超时按「不可达」处理（保守路线，宁可频繁切换也不让 worker 长时间不响应）。
///
/// r3 P2-r3.17：探测并行化 + sweep 总预算 50s 卡死返回
///   旧实现：foreach 串行 await ProbeAsync(5s)；当 13+ 共享同时超时（5s × 13 > 60s）时下一周期 tick 会重叠/积压
///   新实现：所有 folder 探测 Task.WhenAll 并发跑，sweep 内置 50s 总预算（小于 60s 周期）；
///          超总预算后仍未返回的探测一律按「不可达」结算（保守路线，与单 folder 超时策略一致）
///          注意：Task.WhenAll 只跑「无 DB 依赖」的 ProbeAsync；DbContext 读 / SaveChanges 仍串行做，避免触发 §八红线
///
/// 60s 周期由 PeriodicTimer 驱动；StopAsync 时 stoppingToken 取消，PeriodicTimer.WaitForNextTickAsync 抛 OCE 优雅退出。
/// 单次 sweep 内部异常吞咽：单文件夹检测失败不阻断其他文件夹。
/// </remarks>
public sealed class NetworkShareMonitorWorker : BackgroundService
{
    /// <summary>默认扫描周期：60 秒（需求文档对应）</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(1);

    /// <summary>单目录探测超时：5 秒（防 UNC 死路径阻塞 30s）</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>单次 sweep 总预算：50 秒（小于 60s 周期，避免相邻 tick 重叠/积压）</summary>
    public static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(50);

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly ILogger<NetworkShareMonitorWorker> _logger;
    private readonly TimeSpan _period;
    private readonly IAlertService? _alert;
    private readonly IWatchRebuildSignal? _rebuildSignal;

    public NetworkShareMonitorWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IClock clock,
        ILogger<NetworkShareMonitorWorker> logger,
        IAlertService alert,
        IWatchRebuildSignal rebuildSignal)
        : this(dbFactory, clock, logger, DefaultPeriod, alert, rebuildSignal) { }

    /// <summary>测试构造器：自定义 period 加速测试（alert / rebuildSignal 默认 null = 不发告警 / 不发重建信号）</summary>
    internal NetworkShareMonitorWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IClock clock,
        ILogger<NetworkShareMonitorWorker> logger,
        TimeSpan period,
        IAlertService? alert = null,
        IWatchRebuildSignal? rebuildSignal = null)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _logger = logger;
        _period = period;
        _alert = alert;
        _rebuildSignal = rebuildSignal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NetworkShareMonitorWorker 启动：周期={Period}", _period);

        using PeriodicTimer timer = new(_period);
        // 启动即先扫一次，避免首轮等 60s
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

        _logger.LogInformation("NetworkShareMonitorWorker 退出");
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
            _logger.LogError(ex, "网络共享可达性扫描批次失败（下次周期继续）");
        }
    }

    /// <summary>internal 暴露给测试直连，避免依赖 PeriodicTimer 时序</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        // 仅探测「自动监控」的网络盘：AutoScan=false 时无实时 watcher 需重建、也无自动补扫，探测无意义
        List<WatchFolder> folders = await db.WatchFolders
            .Where(f => f.Enabled && f.IsNetworkShare && f.AutoScan)
            .ToListAsync(ct);

        if (folders.Count == 0)
        {
            return 0;
        }

        DateTimeOffset now = _clock.UtcNow;

        // r3 P2-r3.17：sweep 总预算 CTS（与外部停机 token 联动）
        // 超 SweepBudget 时所有还在跑的 ProbeAsync 会被取消（内部包成 unreachable 结算），sweep 整体在 ~50s 内返回；
        // 即使下游 SMB 协商批量挂死，相邻 60s tick 之间仍有 10s 缓冲，不会发生 PeriodicTimer 重叠 / 积压
        using CancellationTokenSource budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(SweepBudget);

        // 并行探测：每个 folder 各自走 ProbeAsync（仅文件系统调用，无 DbContext 共享，不触 §八红线）；
        // Task.WhenAll 让 N 个超时探测的总耗时由「最慢一个」决定而非「累加」，与旧 foreach 比节省 (N-1)*ProbeTimeout
        // 两个 token：budgetCts.Token 让 budget 触发 → 探测内 catch 当不可达；ct 外部停机 token → 透传上抛
        Task<(WatchFolder Folder, bool Reachable)>[] probes = folders
            .Select(async f =>
            {
                bool reachable = await ProbeAsync(f.Path, budgetCts.Token, ct);
                return (f, reachable);
            })
            .ToArray();

        (WatchFolder Folder, bool Reachable)[] results;
        try
        {
            results = await Task.WhenAll(probes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 外部停机：透传给 SweepSafelyAsync 上层
            throw;
        }
        // 注意：budgetCts.IsCancellationRequested 走的是 ProbeAsync 内 catch 路径（不会冒到这里），
        // 该路径已把超 budget 的探测当作 unreachable 落地 —— Task.WhenAll 不会因为 budget 超时而抛

        int probed = 0;
        foreach ((WatchFolder folder, bool reachable) in results)
        {
            if (reachable)
            {
                folder.MarkReachable(now);
            }
            else
            {
                folder.MarkUnreachable(now);
            }
            probed++;
            await EmitReachabilityEventsAsync(folder, ct);
        }

        await db.SaveChangesAsync(ct);
        return probed;
    }

    /// <param name="path">要探测的目录路径</param>
    /// <param name="probeToken">linked token（外部停机 + sweep budget）→ 用于实际取消 Task.Run / WaitAsync</param>
    /// <param name="externalToken">外部停机原始 token → 决定 OCE 是否向上抛</param>
    private async Task<bool> ProbeAsync(string path, CancellationToken probeToken, CancellationToken externalToken)
    {
        try
        {
            // Directory.Exists 是同步阻塞，包 Task.Run 防 UNC 死路径卡 worker；
            // WaitAsync 短超时让单 folder 探测最多耗 ProbeTimeout（linked token，budget 超时也会触发）
            Task<bool> probe = Task.Run(() => Directory.Exists(path), probeToken);
            return await probe.WaitAsync(ProbeTimeout, probeToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("网络共享探测超时（视为不可达）：Path={Path}", path);
            return false;
        }
        catch (OperationCanceledException) when (externalToken.IsCancellationRequested)
        {
            // 外部 stoppingToken 取消：透传给上层让 SweepSafelyAsync 走停机路径
            throw;
        }
        catch (OperationCanceledException)
        {
            // budgetCts 触发（外部 token 未取消）：sweep 总预算超出 → 把当前探测当作不可达落地，
            // 不向上抛打断其它 folder 的 WhenAll
            _logger.LogWarning("网络共享探测被 sweep 总预算（{Budget}）截停（视为不可达）：Path={Path}", SweepBudget, path);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "网络共享探测异常（视为不可达）：Path={Path}", path);
            return false;
        }
    }

    private async Task EmitReachabilityEventsAsync(WatchFolder folder, CancellationToken ct)
    {
        foreach (PersonalMediaManager.Domain.Common.IDomainEvent evt in folder.DomainEvents)
        {
            if (evt is WatchFolderReachabilityChangedEvent r)
            {
                _logger.LogInformation(
                    "WatchFolder 可达性变化：FolderId={FolderId}, Path={Path}, IsReachable={IsReachable}",
                    r.WatchFolderId, r.Path, r.IsReachable);
                if (r.IsReachable)
                {
                    // 恢复可达（翻转沿）→ 通知 FileWatcherWorker 重建该目录 watcher 并触发一次全量补扫；
                    // 不发信号则 UNC 断开期间已失效的 FSW 恢复后不再产生任何事件（监控静默死亡）
                    _rebuildSignal?.Publish(new WatchRebuildItem(WatchChangeKind.ShareRecovered, r.WatchFolderId, r.Path));
                }
                else
                {
                    // 掉线（翻转沿）→ 通知 FileWatcherWorker 暂停该目录监控（卸载死 FSW，恢复后再重建）
                    _rebuildSignal?.Publish(new WatchRebuildItem(WatchChangeKind.ShareLost, r.WatchFolderId, r.Path));
                }
                // 掉线（聚合仅在状态真翻转时产事件，天然防抖）→ 主动告警；恢复不告警，避免噪音
                if (!r.IsReachable && _alert is not null)
                {
                    await _alert.RaiseAsync(
                        $"{WebhookEvents.ShareUnreachable}:{r.WatchFolderId}",
                        WebhookEvents.ShareUnreachable,
                        new { watchFolderId = r.WatchFolderId, path = r.Path, occurredAt = r.OccurredAt },
                        ct);
                }
            }
        }
    }
}
