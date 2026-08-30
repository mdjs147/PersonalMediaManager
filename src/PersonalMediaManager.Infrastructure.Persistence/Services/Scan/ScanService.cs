using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Scan;

/// <summary>扫描服务（D7.6）— 全量 / 单目录扫描入主管线，并实现 IFullScanCoordinator 给 Quartz Job 复用</summary>
/// <remarks>
/// 文件识别：白名单后缀（视频常见 12 种），递归遍历子目录。
/// 去重：DB 中已有同 SourcePath MediaItem（任何状态）→ 跳过，让 History.Rescan 走显式路径，避免覆盖现有进度。
/// 全局锁：静态 InterlockedCAS 标记一次只允许 1 个扫描运行，并发触发抛「已有扫描在进行中」。
/// 全量与单目录共享 _inFlight 锁——FullScanJob 周期触发与用户手动触发互斥。
///
/// 失败策略：
///   - 顶层校验失败（folder 不存在 / 禁用 / 不可达）→ 抛 BusinessException
///   - 单文件入队失败（DB 异常 / channel 满取消）→ 仅记 Warning，继续下个文件
///   - 单目录枚举失败（权限）→ 同上
/// </remarks>
internal sealed class ScanService : IScanService, IFullScanCoordinator
{
    // 视频白名单与忽略规则统一收口到 FileIntakeService.AdmitAsync（单一准入入口）；
    // 此处仅保留同一份白名单做枚举期预筛，避免为非视频文件白白创建 DbContext。
    // 通过 IMediaExtensionProvider 读取动态缓存，CRUD 变更后缓存自动刷新，防漂移。
    private static int _inFlight;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPendingFileQueue _queue;
    private readonly IFileIntakeService _intake;
    private readonly IMediaExtensionProvider _extensionProvider;
    private readonly ILogger<ScanService> _logger;

    public ScanService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IPendingFileQueue queue,
        IFileIntakeService intake,
        IMediaExtensionProvider extensionProvider,
        ILogger<ScanService> logger)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _intake = intake;
        _extensionProvider = extensionProvider;
        _logger = logger;
    }

    // ---------- IFullScanCoordinator（被 D.5.2 FullScanJob 调用）----------

    /// <summary>FullScanJob 周期触发入口 — 零目录场景 noop（区别于手动触发抛 BusinessException）</summary>
    /// <remarks>
    /// B1（F.4 冒烟发现）：与 TriggerAllAsync 共享扫描主流程，但「零启用监控目录」语义分歧：
    ///   - 手动触发（POST /scan/trigger）零目录 → 抛 BusinessException，让 UI 提示「先配置监控目录」
    ///   - 周期触发（Quartz FullScanJob）零目录 → LogInfo + 直接返回，让 ScheduledTaskRunRecorder 落 Succeeded + Processed=0
    /// 否则 Audit_ScheduledTaskRun.Outcome=Failed + [ERR] 日志会成为零配置场景的误告警噪声（首启用户必踩坑）。
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AcquireLock();
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            // 周期触发只覆盖「自动监控」目录：AutoScan=false 的目录靠用户手动扫描，不进周期任务
            List<WatchFolder> folders = await ReadEnabledFoldersAsync(db, autoScanOnly: true, cancellationToken);

            if (folders.Count == 0)
            {
                _logger.LogInformation("FullScanJob 周期触发：无启用且开启自动监控的目录，本轮 noop（Recorder 应落 Succeeded + Processed=0）");
                return;
            }

            // Quartz 周期触发永远走非 force 路径：自动批量重投属于用户显式触发的语义，避免后台定时任务悄悄改 Failed 记录状态
            _ = await ScanFoldersAsync(folders, PendingFileSource.FullScan, force: false, db, cancellationToken);
        }
        finally
        {
            ReleaseLock();
        }
    }

    // ---------- IScanService ----------

    public async Task<ScanTriggerResult> TriggerAllAsync(bool force = false, CancellationToken ct = default)
    {
        AcquireLock();
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            // 手动「立即扫描全部」是用户显式动作，覆盖全部 Enabled 目录（含 AutoScan=false），不受自动监控开关限制
            List<WatchFolder> folders = await ReadEnabledFoldersAsync(db, autoScanOnly: false, ct);

            if (folders.Count == 0)
            {
                // 手动 UI 触发零目录 = 用户引导信号，抛 BusinessException 让前端弹「先配置监控目录」
                // Quartz Job 走的是 RunAsync 的 noop 分支，不会进到这里
                throw new BusinessException("未配置任何监控目录");
            }

            return await ScanFoldersAsync(folders, PendingFileSource.FullScan, force, db, ct);
        }
        finally
        {
            ReleaseLock();
        }
    }

    public async Task<ScanFolderResult> ScanFolderAsync(long folderId, CancellationToken ct = default)
    {
        AcquireLock();
        try
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            WatchFolder? folder = await db.WatchFolders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == folderId, ct);
            if (folder is null) throw new BusinessException("监控目录不存在");
            if (!folder.Enabled) throw new BusinessException("目录已禁用");
            if (!Directory.Exists(folder.Path)) throw new BusinessException("目录不可达（网络共享异常）");

            string scanId = Guid.NewGuid().ToString("N");
            // 单目录扫描保持非 force 语义：用户要批量重投失败记录，请走顶部「强制全扫」
            (int enq, _) = await ScanOneFolderAsync(folder.Id, folder.Path, PendingFileSource.Manual, force: false, db, ct);

            _logger.LogInformation("单目录扫描完成：ScanId={ScanId}, FolderId={Id}, Files={Files}",
                scanId, folder.Id, enq);
            return new ScanFolderResult(scanId, folder.Id, folder.Path, enq);
        }
        finally
        {
            ReleaseLock();
        }
    }

    public async Task<ScanPathResult> ScanPathAsync(string path, CancellationToken ct = default)
    {
        // 路径非空校验已上移至 ScanPathRequest DataAnnotations，模型绑定阶段拦截；
        // 此处对 null 仅做防御性归一为空串，后续走「路径不存在」分支
        string normalized = path?.Trim() ?? string.Empty;

        bool isDir = Directory.Exists(normalized);
        bool isFile = !isDir && File.Exists(normalized);
        if (!isDir && !isFile)
            throw new BusinessException("路径不存在");

        // 单文件后缀校验前置到抢锁之前，避免非视频文件无意义占用全局扫描锁
        if (isFile && !_extensionProvider.GetEnabledExtensions().Contains(Path.GetExtension(normalized)))
            throw new BusinessException("不是受支持的视频文件");

        AcquireLock();
        try
        {
            string scanId = Guid.NewGuid().ToString("N");

            IReadOnlyList<string> files = isFile
                ? new[] { normalized }
                : EnumerateFilesRobustly(normalized, ct);

            int enqueued = 0;
            int skipped = 0;
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (!_extensionProvider.GetEnabledExtensions().Contains(Path.GetExtension(file))) continue;

                try
                {
                    // 登记 + 入队（IFileIntakeService 按 SourcePath 幂等：已存在则返回 false 计入 Skipped，重投走 History.Rescan 显式路径）
                    // WatchFolderId=0：手动整理不绑定监控目录（多层路径上下文按 ProcessFileService 退化为单层父目录）
                    if (await _intake.AdmitAsync(file, 0, PendingFileSource.Manual, ct))
                        enqueued++;
                    else
                        skipped++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "手动整理入队失败（跳过单条）：{Path}", file);
                }
            }

            _logger.LogInformation(
                "手动整理完成：ScanId={ScanId}, Path={Path}, IsDir={IsDir}, Enqueued={Enq}, Skipped={Skip}",
                scanId, normalized, isDir, enqueued, skipped);
            return new ScanPathResult(scanId, normalized, isDir, enqueued, skipped);
        }
        finally
        {
            ReleaseLock();
        }
    }

    // ---------- 内部：共享扫描主流程 ----------

    /// <summary>读取启用监控目录（按 Priority 升序），供 RunAsync/TriggerAllAsync 共用</summary>
    /// <param name="autoScanOnly">
    /// true：仅取 AutoScan=true 的目录（周期 FullScanJob 走此路径——「自动监控」语义）；
    /// false：取全部 Enabled 目录（用户手动「立即扫描全部」属显式触发，不受 AutoScan 限制）。
    /// </param>
    private static async Task<List<WatchFolder>> ReadEnabledFoldersAsync(PmmDbContext db, bool autoScanOnly, CancellationToken ct)
    {
        return await db.WatchFolders.AsNoTracking()
            .Where(f => f.Enabled && (!autoScanOnly || f.AutoScan))
            .OrderBy(f => f.Priority)
            .ToListAsync(ct);
    }

    /// <summary>遍历目录列表执行扫描，单目录失败不影响其他</summary>
    /// <remarks>
    /// 调用方负责锁 / DbContext 生命周期 / 零目录分支决策。
    /// force=true 时 ScanOneFolderAsync 会对已存在的 Failed 记录自动 Rescan；
    /// FilesEnqueued 统计「新文件入队 + force 模式下 Rescan 重投」之和，FailedRescanned 单独披露后者。
    /// </remarks>
    private async Task<ScanTriggerResult> ScanFoldersAsync(
        List<WatchFolder> folders, PendingFileSource source, bool force, PmmDbContext db, CancellationToken ct)
    {
        string scanId = Guid.NewGuid().ToString("N");
        int totalEnqueued = 0;
        int totalRescanned = 0;
        foreach (WatchFolder folder in folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                _logger.LogWarning("扫描跳过不可达目录：FolderId={Id}, Path={Path}", folder.Id, folder.Path);
                continue;
            }
            (int enq, int rescanned) = await ScanOneFolderAsync(folder.Id, folder.Path, source, force, db, ct);
            totalEnqueued += enq;
            totalRescanned += rescanned;
        }

        _logger.LogInformation(
            "{Mode}扫描完成：ScanId={ScanId}, FolderCount={Folders}, Files={Files}, FailedRescanned={Rescanned}",
            force ? "强制" : "全量", scanId, folders.Count, totalEnqueued, totalRescanned);
        return new ScanTriggerResult(scanId, folders.Count, totalEnqueued, totalRescanned);
    }

    // ---------- 内部：枚举 + 去重 + 入队 ----------

    /// <summary>枚举单个监控目录，按 force 决策已存在 MediaItem 的处理方式</summary>
    /// <returns>(新入队 + 重投 Rescan 之和, 其中 Rescan 数量)</returns>
    private async Task<(int Enqueued, int Rescanned)> ScanOneFolderAsync(
        long folderId, string rootPath, PendingFileSource source, bool force,
        PmmDbContext db, CancellationToken ct)
    {
        IReadOnlyList<string> files = EnumerateFilesRobustly(rootPath, ct);

        int enqueued = 0;
        int rescanned = 0;
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            string ext = Path.GetExtension(file);
            if (!_extensionProvider.GetEnabledExtensions().Contains(ext)) continue;

            // force=true：已存在记录中仅 Failed 自动重投（库里已有行，原地 Transition 回 Queued 重投，不经 IFileIntakeService）；
            // 其余状态跳过。existing 为 null（全新文件）时落到下方统一登记入队。
            if (force)
            {
                MediaItem? existing = await db.MediaItems
                    .FirstOrDefaultAsync(m => m.SourcePath == file, ct);
                if (existing is not null)
                {
                    // 仅 Failed 自动重投：与 HistoryService.RescanAsync §6.2 状态机硬约束一致，
                    // Queued/Processing 已在管线中、Succeeded/Archived 强制重置会破坏审计链 → 一律跳过
                    if (existing.Status != MediaItemStatus.Failed)
                    {
                        _logger.LogDebug("强制全扫：已存在且非 Failed，跳过：{Path}, Status={Status}",
                            file, existing.Status);
                        continue;
                    }

                    try
                    {
                        existing.Transition(MediaItemStatus.Queued);
                        existing.ClearError();
                        await db.SaveChangesAsync(ct);
                        await _queue.EnqueueAsync(new PendingFileItem(file, folderId, source), ct);
                        enqueued++;
                        rescanned++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "强制全扫重投失败（跳过单条）：{Path}", file);
                    }
                    continue;
                }
            }

            try
            {
                // 新文件（force / 非 force 共用）：IFileIntakeService 先建 Detected 行落库再入队，
                // 已存在同 SourcePath 由它幂等跳过（取代旧的 AnyAsync 预查），让「处理队列」页与重启恢复都能看到积压
                if (await _intake.AdmitAsync(file, folderId, source, ct))
                    enqueued++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "入队失败（跳过单条）：{Path}", file);
            }
        }

        return (enqueued, rescanned);
    }

    /// <summary>健壮递归枚举单目录下所有文件，不可读子目录跳过、残余 IO 异常不中断整次扫描</summary>
    /// <remarks>
    /// IgnoreInaccessible=true 让递归遇到不可读子目录（权限 / reparse point / 路径过长 / 扫描中被删等）
    /// 时自动跳过该子目录而非抛出——这是旧 Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories)
    /// 走 Compatible 模式（IgnoreInaccessible=false）的缺陷根因：惰性枚举的真正遍历发生在 MoveNext 内，
    /// 任一坏子目录会在调用方 foreach 里抛 UnauthorizedAccessException/IOException 逃逸、令整次扫描中断。
    /// 显式 AttributesToSkip=None + MatchType=Win32 对齐旧 SearchOption 行为（默认 EnumerationOptions 会
    /// 跳过 Hidden/System 文件，否则隐藏属性的视频会被静默漏扫）。
    /// 对 MoveNext 再包一层防御：IgnoreInaccessible 未能消化的残余 IO 异常（枚举器此刻已不可继续）仅记
    /// Warning 并终止本目录剩余枚举，已收集文件与其他监控目录均不受影响。
    /// </remarks>
    private IReadOnlyList<string> EnumerateFilesRobustly(string rootPath, CancellationToken ct)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            MatchType = MatchType.Win32,
        };

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFiles(rootPath, "*", options).GetEnumerator();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举目录失败：{Path}", rootPath);
            return Array.Empty<string>();
        }

        List<string> files = new();
        using (enumerator)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (!enumerator.MoveNext()) break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "枚举目录中途失败（已收集 {Count} 文件，跳过剩余）：{Path}", files.Count, rootPath);
                    break;
                }

                files.Add(enumerator.Current);
            }
        }

        return files;
    }

    private static void AcquireLock()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            throw new BusinessException("已有扫描在进行中");
        }
    }

    private static void ReleaseLock()
    {
        Interlocked.Exchange(ref _inFlight, 0);
    }

    /// <summary>测试用：把 in-flight 锁手动复位（避免测试间状态泄漏）</summary>
    internal static void ResetInFlightForTests()
    {
        Interlocked.Exchange(ref _inFlight, 0);
    }
}
