using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>FileWatcherWorker（D6.1）— 为每个启用的 WatchFolder 拉起一个 IFileWatcher 并把事件灌入主管线</summary>
/// <remarks>
/// 生命周期：
///   StartAsync：用 IDbContextFactory 取独立 DbContext（§0.5 红线：不复用主 DbContext 防 Task.WhenAll 冲突），
///     枚举所有 Enabled=true 且 AutoScan=true 的 WatchFolder，按 Priority 升序为每个目录 IFileWatcherFactory.Create(path) 拿 IFileWatcher，
///     订阅 OnFileEvent 后 watcher.Start()；随后进入 IWatchRebuildSignal 信号消费循环（不再是傻等 Task.Delay）。
///   StopAsync：Dispose 所有 watcher，触发 IFileWatcher 内部解订阅 + 释放 FSW。
///
/// 信号驱动的增量重建（修复「启动后目录集冻结」与「网络盘恢复监控静默死亡」）：
///   - FolderCreated / FolderUpdated：按 DB 当前状态对账 —— 启用且开启自动监控则（重）挂载，禁用 / 关闭自动监控 / 不存在则卸载；
///   - FolderDeleted / ShareLost：立即卸载该目录 watcher（禁用目录不再继续采集、死 FSW 不再挂着）；
///   - ShareRecovered：重建 watcher 并触发该目录一次全量补扫（断线期间落入的文件靠补扫追平）；
///   - WatcherFaulted：FSW Error（缓冲溢出 / 监控失效）按 Path 反查目录后重建 + 补扫（溢出意味着已丢事件）。
///   补扫复用 IScanService.ScanFolderAsync（Scoped，经 IServiceScopeFactory 开作用域解析）；
///   撞上「已有扫描在进行中」等业务性失败时按固定间隔重试数次后放弃（周期 FullScanJob 仍可兜底）。
///
/// 事件过滤：
///   只关心 Created / Renamed（新文件落入目录）；
///   Changed 在写入完成检测里处理，不入队（避免 5MB 文件触发 100 次 Changed 把队列灌满）；
///   Deleted 不入队（已被移走的文件无需处理）。
///
/// 失败兜底：
///   单个 WatchFolder.Path 不存在 / 无权限 → 仅记 Warning 跳过，不影响其他目录
///   （恢复可达后由 ShareRecovered 信号补挂载，启动期跳过不再是永久失明）；
///   网络共享可达性探测由 NetworkShareMonitorWorker（D6.4）负责，本 Worker 只消费其翻转信号。
///
/// 路径不存在的目录跳过原因：
///   FSW 在路径不存在时构造就抛 ArgumentException；让 worker 在迁移环境 / 临时拔网线场景下也能启动。
/// </remarks>
public sealed class FileWatcherWorker : BackgroundService
{
    /// <summary>补扫撞「已有扫描在进行中」等业务性失败时的最大尝试次数</summary>
    internal const int MaxScanAttempts = 3;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IFileWatcherFactory _watcherFactory;
    private readonly IFileIntakeService _intake;
    private readonly IWatchRebuildSignal _rebuildSignal;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileWatcherWorker> _logger;
    private readonly TimeSpan _scanRetryDelay;
    private readonly object _gate = new();
    private readonly List<RegisteredWatcher> _watchers = new();

    public FileWatcherWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IFileWatcherFactory watcherFactory,
        IFileIntakeService intake,
        IWatchRebuildSignal rebuildSignal,
        IServiceScopeFactory scopeFactory,
        ILogger<FileWatcherWorker> logger)
        : this(dbFactory, watcherFactory, intake, rebuildSignal, scopeFactory, logger, TimeSpan.FromSeconds(30)) { }

    /// <summary>测试构造器：自定义补扫重试间隔，避免单测等真实 30s</summary>
    internal FileWatcherWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IFileWatcherFactory watcherFactory,
        IFileIntakeService intake,
        IWatchRebuildSignal rebuildSignal,
        IServiceScopeFactory scopeFactory,
        ILogger<FileWatcherWorker> logger,
        TimeSpan scanRetryDelay)
    {
        _dbFactory = dbFactory;
        _watcherFactory = watcherFactory;
        _intake = intake;
        _rebuildSignal = rebuildSignal;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _scanRetryDelay = scanRetryDelay;
    }

    /// <summary>已注册的 watcher 数量（测试用）</summary>
    internal int RegisteredCount
    {
        get { lock (_gate) { return _watchers.Count; } }
    }

    private readonly TaskCompletionSource _startupRegistration = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>启动期初始注册完成信号（测试用：等首轮挂载结束再发重建信号，避免与初始枚举竞态）</summary>
    internal Task StartupRegistrationCompleted => _startupRegistration.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(stoppingToken);
            // 仅挂载「自动监控」目录：AutoScan=false 的目录不做实时监听，靠用户手动扫描
            List<WatchFolder> folders = await db.WatchFolders
                .Where(f => f.Enabled && f.AutoScan)
                .OrderBy(f => f.Priority)
                .ToListAsync(stoppingToken);

            _logger.LogInformation("FileWatcherWorker 启动：发现 {Total} 个启用且开启自动监控的目录", folders.Count);

            foreach (WatchFolder folder in folders)
            {
                TryRegister(folder.Id, folder.Path, stoppingToken);
            }
        }
        finally
        {
            _startupRegistration.TrySetResult();
        }

        try
        {
            // 信号消费循环：目录 CRUD / 网络共享可达性翻转 / FSW 内部故障 → 增量重建对应 watcher；
            // 取消时 ReadAsync 抛 OCE 优雅退出，StopAsync 随后统一释放全部 watcher
            while (!stoppingToken.IsCancellationRequested)
            {
                WatchRebuildItem signal = await _rebuildSignal.Reader.ReadAsync(stoppingToken);
                await HandleRebuildSignalAsync(signal, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关停
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 先停消费循环（base.StopAsync 取消 stoppingToken 并等 ExecuteAsync 退出），
        // 再统一释放 watcher——反过来会有「已释放后信号又重建出新 watcher」的关停竞态；
        // finally 保证即使 base 等待被取消，已注册 watcher 也不泄漏
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            RegisteredWatcher[] snapshot;
            lock (_gate)
            {
                snapshot = _watchers.ToArray();
                _watchers.Clear();
            }
            foreach (RegisteredWatcher reg in snapshot)
            {
                try
                {
                    reg.Watcher.OnFileEvent -= reg.Handler;
                    reg.Watcher.Dispose();
                }
                catch (Exception ex)
                {
                    // FSW Dispose 为同步释放，不应抛 OCE；任何释放失败仅记 Warning，不打断其余 watcher 释放
                    _logger.LogWarning(ex, "释放 watcher 失败：FolderId={FolderId}, Path={Path}", reg.FolderId, reg.Path);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 信号处理：增量挂载 / 卸载 / 重建 / 补扫
    // ──────────────────────────────────────────────────────────────

    /// <summary>处理单条重建信号（internal 暴露给测试直连，避免依赖消费循环时序）</summary>
    internal async Task HandleRebuildSignalAsync(WatchRebuildItem signal, CancellationToken ct)
    {
        try
        {
            switch (signal.Kind)
            {
                case WatchChangeKind.FolderDeleted:
                    if (Unregister(signal.FolderId))
                    {
                        _logger.LogInformation("监控目录已删除，watcher 已卸载：FolderId={FolderId}", signal.FolderId);
                    }
                    break;

                case WatchChangeKind.ShareLost:
                    if (Unregister(signal.FolderId))
                    {
                        _logger.LogWarning("网络共享不可达，已暂停该目录监控（恢复后自动重建并补扫）：FolderId={FolderId}, Path={Path}",
                            signal.FolderId, signal.Path);
                    }
                    break;

                case WatchChangeKind.FolderCreated:
                case WatchChangeKind.FolderUpdated:
                    await ReconcileAsync(signal.FolderId, ct);
                    break;

                case WatchChangeKind.ShareRecovered:
                {
                    _logger.LogInformation("网络共享恢复可达，开始重建监控并补扫：FolderId={FolderId}, Path={Path}",
                        signal.FolderId, signal.Path);
                    bool mounted = await ReconcileAsync(signal.FolderId, ct);
                    if (mounted)
                    {
                        // 断线期间落入的文件 FSW 不可能感知，靠一次该目录全量补扫追平
                        await TriggerFolderScanAsync(signal.FolderId, ct);
                    }
                    break;
                }

                case WatchChangeKind.WatcherFaulted:
                {
                    long folderId = signal.FolderId != 0 ? signal.FolderId : FindFolderIdByPath(signal.Path);
                    if (folderId == 0)
                    {
                        _logger.LogWarning("FSW 故障信号无法定位监控目录（可能已被卸载），忽略：Path={Path}", signal.Path);
                        break;
                    }
                    _logger.LogWarning("FSW 内部故障，开始重建监控：FolderId={FolderId}, Path={Path}", folderId, signal.Path);
                    bool mounted = await ReconcileAsync(folderId, ct);
                    if (mounted)
                    {
                        // 缓冲溢出 / 监控失效期间可能已丢事件，补扫一次兜底
                        await TriggerFolderScanAsync(folderId, ct);
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理监控重建信号失败：Kind={Kind}, FolderId={FolderId}, Path={Path}",
                signal.Kind, signal.FolderId, signal.Path);
        }
    }

    /// <summary>按 DB 当前状态对账单个目录：先卸载旧 watcher，启用则重新挂载；返回是否挂载成功</summary>
    private async Task<bool> ReconcileAsync(long folderId, CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        WatchFolder? folder = await db.WatchFolders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == folderId, ct);

        Unregister(folderId);

        if (folder is null || !folder.Enabled || !folder.AutoScan)
        {
            _logger.LogInformation("监控目录已删除 / 禁用 / 关闭自动监控，不再挂载：FolderId={FolderId}", folderId);
            return false;
        }
        return TryRegister(folder.Id, folder.Path, ct);
    }

    /// <summary>触发单目录全量补扫；撞业务性失败（如「已有扫描在进行中」）按固定间隔重试后放弃</summary>
    private async Task TriggerFolderScanAsync(long folderId, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxScanAttempts; attempt++)
        {
            try
            {
                // IScanService 注册为 Scoped，单例 Worker 内必须开作用域解析（只调用现成扫描入口，不自造扫描逻辑）
                using IServiceScope scope = _scopeFactory.CreateScope();
                IScanService scan = scope.ServiceProvider.GetRequiredService<IScanService>();
                ScanFolderResult result = await scan.ScanFolderAsync(folderId, ct);
                _logger.LogInformation("监控重建补扫完成：FolderId={FolderId}, 新入队 {Files} 个文件", folderId, result.FilesEnqueued);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BusinessException ex)
            {
                // 「已有扫描在进行中」属瞬态冲突，等一拍重试；「目录已禁用 / 不可达」重试同样自然终止（次数耗尽放弃）
                _logger.LogWarning("监控重建补扫未执行（第 {Attempt}/{Max} 次）：FolderId={FolderId}，原因：{Reason}",
                    attempt, MaxScanAttempts, folderId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "监控重建补扫异常（放弃，待周期全量扫描兜底）：FolderId={FolderId}", folderId);
                return;
            }

            if (attempt < MaxScanAttempts)
            {
                await Task.Delay(_scanRetryDelay, ct);
            }
        }
        _logger.LogWarning("监控重建补扫放弃（重试 {Max} 次后仍未执行，待周期全量扫描兜底）：FolderId={FolderId}", MaxScanAttempts, folderId);
    }

    // ──────────────────────────────────────────────────────────────
    // watcher 注册 / 卸载
    // ──────────────────────────────────────────────────────────────

    private bool TryRegister(long folderId, string path, CancellationToken stoppingToken)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("跳过监控目录（不存在或不可达）：FolderId={FolderId}, Path={Path}", folderId, path);
            return false;
        }

        try
        {
            IFileWatcher watcher = _watcherFactory.Create(path);
            Action<FileWatcherEvent> handler = evt => OnFileEvent(folderId, evt, stoppingToken);
            watcher.OnFileEvent += handler;
            watcher.Start();
            lock (_gate)
            {
                _watchers.Add(new RegisteredWatcher(folderId, path, watcher, handler));
            }
            _logger.LogInformation("已为监控目录注册 watcher：FolderId={FolderId}, Path={Path}", folderId, path);
            return true;
        }
        catch (Exception ex)
        {
            // 取消信号不能被吞：让 ExecuteAsync 走正常停机路径
            if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            _logger.LogError(ex, "注册 watcher 失败：FolderId={FolderId}, Path={Path}", folderId, path);
            return false;
        }
    }

    /// <summary>卸载指定目录的 watcher（解订阅 + Dispose）；未注册时返回 false</summary>
    private bool Unregister(long folderId)
    {
        RegisteredWatcher? reg;
        lock (_gate)
        {
            reg = _watchers.FirstOrDefault(w => w.FolderId == folderId);
            if (reg is null)
            {
                return false;
            }
            _watchers.Remove(reg);
        }

        try
        {
            reg.Watcher.OnFileEvent -= reg.Handler;
            reg.Watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 watcher 失败：FolderId={FolderId}, Path={Path}", reg.FolderId, reg.Path);
        }
        return true;
    }

    /// <summary>按路径反查已注册 watcher 的 FolderId（WatcherFaulted 信号来自 FSW 回调，拿不到 Id）</summary>
    private long FindFolderIdByPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }
        lock (_gate)
        {
            return _watchers.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase))
                ?.FolderId ?? 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 文件事件 → 主管线入队
    // ──────────────────────────────────────────────────────────────

    private void OnFileEvent(long folderId, FileWatcherEvent evt, CancellationToken stoppingToken)
    {
        // 只关心新文件出现：Created（新建）+ Renamed（重命名到目录内或目录内改名）
        if (evt.ChangeType is not (FileWatcherChangeType.Created or FileWatcherChangeType.Renamed))
        {
            return;
        }

        // 目录事件忽略；FileSystemWatcher 对子目录 Created 也会触发
        if (Directory.Exists(evt.FullPath))
        {
            return;
        }

        // 入队按 fire-and-forget；ValueTask 等待但不阻塞 FSW 回调线程过久
        _ = EnqueueSafe(folderId, evt.FullPath, stoppingToken);
    }

    private async Task EnqueueSafe(long folderId, string fullPath, CancellationToken stoppingToken)
    {
        try
        {
            // 经 IFileIntakeService 先建 Detected 行落库再入队：让「处理队列」页能看到该文件、进程重启也能从库恢复
            await _intake.AdmitAsync(fullPath, folderId, PendingFileSource.Watcher, stoppingToken);
            _logger.LogDebug("登记入队：FolderId={FolderId}, Path={Path}", folderId, fullPath);
        }
        catch (OperationCanceledException)
        {
            // 正常关停
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记入队失败：FolderId={FolderId}, Path={Path}", folderId, fullPath);
        }
    }

    private sealed record RegisteredWatcher(long FolderId, string Path, IFileWatcher Watcher, Action<FileWatcherEvent> Handler);
}
