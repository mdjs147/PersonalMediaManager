using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>新发现文件登记 + 入队实现</summary>
/// <remarks>
/// 用 IDbContextFactory 每次自建独立 DbContext（无 Scoped 依赖 → 可注册 Singleton，供 Singleton 的 FileWatcherWorker 直接注入）。
/// 顺序「先落库再入队」：库是事实源、内存 Channel 是加速器；落库成功但入队前进程崩溃，
/// 留下的 Detected 行由 StartupRecoveryWorker 下次启动重排，不丢任务。
/// 幂等：已存在同 SourcePath（任意状态）一律跳过——重投走 History.Rescan / 强制全扫的显式路径，避免覆盖现有进度。
/// 并发：FileWatcher 对同一文件可能连发多次事件，靠 UQ_Media_Item_SourcePath 唯一索引兜底，
///   SaveChanges 撞唯一约束（DbUpdateException）视为「已被另一事件抢先建行」，按幂等跳过。
///
/// 准入策略（监控 / 全扫 / 手动整理共用此单一入口）：
///   1. 非视频扩展名（含下载中多重后缀如 .mp4.bt.xltd → 取 .xltd）直接拒收，不建行不入队；
///   2. 命中启用的忽略规则（扩展名结尾匹配 / 关键词子串）拒收。
/// 修复缺陷：旧实现实时监控不做任何过滤，临时下载文件被放进待确认队列且改名完成后旧记录残留。
/// </remarks>
internal sealed class FileIntakeService : IFileIntakeService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPendingFileQueue _queue;
    private readonly IMediaExtensionProvider _extensionProvider;
    private readonly ILogger<FileIntakeService> _logger;

    public FileIntakeService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IPendingFileQueue queue,
        IMediaExtensionProvider extensionProvider,
        ILogger<FileIntakeService> logger)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _extensionProvider = extensionProvider;
        _logger = logger;
    }

    public async Task<bool> AdmitAsync(
        string fullPath, long watchFolderId, PendingFileSource source, CancellationToken ct = default)
    {
        string fileName = Path.GetFileName(fullPath);

        // 准入策略 1：非视频扩展名直接拒收（多重后缀临时文件如 .mp4.bt.xltd 取末段 .xltd 不在白名单）
        // 在途无任何 DB 访问 → 监控放进来的海量临时文件零成本挡在入队之前
        if (!_extensionProvider.IsVideo(fileName))
        {
            _logger.LogDebug("非视频扩展名，拒收：{Path}", fullPath);
            return false;
        }

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 准入策略 2：命中启用的忽略规则（扩展名结尾 / 关键词子串）→ 拒收，不建行不入队
        List<WatchIgnoreRule> ignoreRules = await db.WatchIgnoreRules.AsNoTracking()
            .Where(r => r.Enabled)
            .ToListAsync(ct);
        WatchIgnoreRule? hit = ignoreRules.FirstOrDefault(r => r.Matches(fileName));
        if (hit is not null)
        {
            _logger.LogDebug("命中忽略规则（{Type}={Pattern}），拒收：{Path}", hit.Type, hit.Pattern, fullPath);
            return false;
        }

        // 幂等去重：已有同 SourcePath（任意状态）→ 跳过，不重复建行 / 不重复入队
        bool exists = await db.MediaItems.AsNoTracking().AnyAsync(m => m.SourcePath == fullPath, ct);
        if (exists)
        {
            _logger.LogDebug("文件已登记，跳过入队：{Path}", fullPath);
            return false;
        }

        // 新文件：先建 Detected 行落库（让「处理队列」页与重启恢复都能看到排队积压），再入队
        FileInfo fi = new(fullPath);
        MediaItem media = MediaItem.CreateDetected(fullPath, fi.Name, fi.Exists ? fi.Length : 0);
        db.MediaItems.Add(media);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // UQ_Media_Item_SourcePath 冲突：并发事件已抢先建行 → 当作幂等跳过（避免重复入队）
            _logger.LogDebug(ex, "并发登记冲突（已被其它事件建行），跳过入队：{Path}", fullPath);
            return false;
        }

        await _queue.EnqueueAsync(new PendingFileItem(fullPath, watchFolderId, source), ct);
        _logger.LogDebug("已登记并入队：MediaItemId={Id}, Source={Source}, Path={Path}", media.Id, source, fullPath);
        return true;
    }
}
