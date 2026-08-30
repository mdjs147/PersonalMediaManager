using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Review;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>启动恢复 Worker（P1-r2.5 + r3 P1-r3.14）— 修复异常关停遗留的状态孤儿</summary>
/// <remarks>
/// 两类孤儿，启动时各处理一次：
///
/// 1. Archiving 孤儿：ConfirmAsync 中 ArchiveAsync 已物理移文件，但随后 SaveChangesAsync 失败（崩溃/DB 瞬断），
///    MediaItem 停在 Archiving，既无 Completed 也无 Failed。双向核验（防两类误判）：
///      - TargetPath 非空且文件存在：再核 FileSize（记录有值时）—— 长度一致 → 推进 Completed；
///        长度不符（TargetPath 指向他人文件 / 冲突跳过遗留的 stale 路径）→ 标 Failed「目标文件与记录不符」，
///        不再被误判为假 Completed；
///      - TargetPath 为空（崩溃在 SetArchiveResult 落库前）：按记录的 ParsedInfo / 分类经
///        IReviewService.PreviewPathsAsync（与实际归档共用 ArchiveFolderResolver 的只读预览）重算预期落点，
///        文件已落地且长度吻合 → 补 TargetPath 并按 Completed 收尾（源已被移走，标 Failed 则 Rescan 因源缺失永不可恢复）；
///      - 其余 → 标 Failed，供用户 Rescan。
///    所有状态改写补时间线步骤；判 Failed 处发 media.failed Webhook（口径对齐 ProcessFileService.EmitFailedAsync）。
///
/// 2. 在途 Queued / 中途态孤儿：处理队列 PendingFileQueue 是【进程内内存 Channel】，重启即丢。
///    已入队但没跑完的记录（Queued / Parsing / TmdbMatching / AiParsing / TmdbRematching / Classifying）
///    不会被任何机制重新拾取（强制扫描只重投 Failed、普通扫描跳过已存在 SourcePath），不处理就永久搁浅。
///      - 统一经 MediaItem.RequeueInterrupted() 重置为 Queued，再重新塞回 PendingFileQueue。
///      - WatchFolderId 按 SourcePath 前缀匹配启用监控目录推导（给多层路径解析提供监控根）。
///
/// 同步 / 后台分界（关键）：
///   - Archiving 恢复 + 在途记录【状态重置 + 落库】走【同步】：保证 FileWatcher 启动前 DB 已干净一致
///     （沿用 r3 P1-r3.14：IHostedService.StartAsync 同步阻塞至完成，Host 才启动后续 Worker）。
///   - 把重置后的记录【重新入队】走【后台 Task】：因 TaskProcessorWorker（消费者）在本 Worker 之后才注册启动，
///     若在 StartAsync 内同步灌入有界 Channel（容量 1024、FullMode=Wait），积压超容量会死锁启动。
///     后台入队用本 Worker 自管的 CTS，StopAsync 时取消并等待，保证优雅关停。
/// </remarks>
public sealed class StartupRecoveryWorker : IHostedService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPendingFileQueue _queue;
    private readonly IWebhookEmitter _webhook;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupRecoveryWorker> _logger;

    private CancellationTokenSource? _requeueCts;
    private Task? _requeueEnqueueTask;

    public StartupRecoveryWorker(
        IDbContextFactory<PmmDbContext> dbFactory,
        IPendingFileQueue queue,
        IWebhookEmitter webhook,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        ILogger<StartupRecoveryWorker> logger)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _webhook = webhook;
        _clock = clock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>后台重排入队任务（测试用：可 await 等其完成后再断言入队结果）</summary>
    internal Task? RequeueEnqueueTask => _requeueEnqueueTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartupRecoveryWorker 启动：恢复 Archiving 孤儿 + 重排在途 Queued 记录（后续 Worker 才会接手）");

        // 1. Archiving 孤儿（同步）
        try
        {
            await RecoverStuckArchivingAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archiving 孤儿恢复失败，跳过该步（不阻断启动）");
        }

        // 2. 在途记录状态重置 + 落库（同步），收集待重排清单
        IReadOnlyList<PendingFileItem> toRequeue = Array.Empty<PendingFileItem>();
        try
        {
            toRequeue = await ResetInterruptedToQueuedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "在途记录重置失败，跳过启动重排（不阻断启动）");
        }

        // 3. 重新入队（后台，不阻塞启动）
        if (toRequeue.Count > 0)
        {
            _requeueCts = new CancellationTokenSource();
            _requeueEnqueueTask = DrainEnqueueAsync(toRequeue, _requeueCts.Token);
        }

        _logger.LogInformation("StartupRecoveryWorker 同步恢复完成（{Count} 条在途记录的重新入队在后台进行）", toRequeue.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_requeueCts is not null)
        {
            await _requeueCts.CancelAsync();
        }
        if (_requeueEnqueueTask is not null)
        {
            try
            {
                await _requeueEnqueueTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 关停取消属正常
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "等待启动重排入队任务收尾时异常");
            }
        }
        _requeueCts?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    // 1. Archiving 孤儿恢复
    // ──────────────────────────────────────────────────────────────

    private async Task RecoverStuckArchivingAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        List<MediaItem> stuckItems = await db.MediaItems
            .Where(m => m.Status == MediaItemStatus.Archiving)
            .ToListAsync(ct);

        if (stuckItems.Count == 0)
        {
            _logger.LogDebug("未发现 Archiving 状态孤儿，跳过恢复");
            return;
        }

        _logger.LogWarning("发现 {Count} 条 Archiving 状态孤儿，开始逐一恢复", stuckItems.Count);

        int completedCount = 0;
        int failedCount = 0;
        // 判 Failed 的 Webhook 留到 SaveChanges 成功后统一发，避免「事件已出、状态未落库」的不一致
        List<(MediaItem Item, string Reason)> failedToEmit = new();

        foreach (MediaItem item in stuckItems)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.TargetPath) && File.Exists(item.TargetPath))
                {
                    // 误判方向 (b) 防护：TargetPath 可能是 stale（指向他人文件 / 历史冲突遗留），
                    // 记录 FileSize 有值时核对长度，不符则不判 Completed，转 Failed 供人工核查
                    long actualLength = TryGetFileLength(item.TargetPath);
                    if (item.FileSize > 0 && actualLength >= 0 && actualLength != item.FileSize)
                    {
                        string reason = "目标文件与记录不符，需人工核查";
                        MarkFailedWithStep(item, reason);
                        failedToEmit.Add((item, reason));
                        _logger.LogWarning(
                            "恢复为 Failed：MediaItemId={Id}，目标文件长度与记录不符（{Actual} ≠ {Expected} 字节）：{TargetPath}",
                            item.Id, actualLength, item.FileSize, item.TargetPath);
                        failedCount++;
                    }
                    else
                    {
                        // 文件已在目标位置且与记录吻合，说明归档操作已完成，只是状态未落库
                        CompleteWithStep(item, "启动恢复：目标文件已存在，归档实际已完成");
                        _logger.LogInformation(
                            "恢复为 Completed：MediaItemId={Id}，目标文件已存在 {TargetPath}",
                            item.Id, item.TargetPath);
                        completedCount++;
                    }
                }
                else if (string.IsNullOrEmpty(item.TargetPath)
                    && await TryResolveExpectedTargetAsync(item, ct) is string expectedTarget
                    && File.Exists(expectedTarget)
                    && (item.FileSize <= 0 || TryGetFileLength(expectedTarget) == item.FileSize))
                {
                    // 误判方向 (a) 防护：崩溃发生在 SetArchiveResult 落库前（TargetPath=null）但视频实际已落地。
                    // 此时源文件已被移走，标 Failed 则 Rescan 因源缺失永不可恢复——按 Completed 收尾并补 TargetPath。
                    item.SetArchiveResult(expectedTarget);
                    CompleteWithStep(item, "启动恢复：按解析信息重算预期落点，目标文件已落地（崩溃发生在落库前），补 TargetPath 收尾");
                    _logger.LogInformation(
                        "恢复为 Completed：MediaItemId={Id}，TargetPath 缺失但重算落点已存在，补写 {TargetPath}",
                        item.Id, expectedTarget);
                    completedCount++;
                }
                else
                {
                    // 文件不在目标位置，归档可能未完成，标 Failed 供用户 Rescan
                    string reason = "系统启动时检测到归档中断，文件未找到目标位置，请在历史记录中重新扫描";
                    MarkFailedWithStep(item, reason);
                    failedToEmit.Add((item, reason));
                    _logger.LogWarning(
                        "恢复为 Failed：MediaItemId={Id}，目标路径={TargetPath}（文件不存在）",
                        item.Id, item.TargetPath ?? "(null)");
                    failedCount++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复单条 MediaItem 失败：Id={Id}", item.Id);
            }
        }

        await db.SaveChangesAsync(ct);

        // 状态已落库，再发 media.failed（IWebhookEmitter 契约自吞异常，不阻断启动）
        foreach ((MediaItem item, string reason) in failedToEmit)
        {
            await EmitFailedAsync(item, reason, ct);
        }

        _logger.LogWarning(
            "StartupRecoveryWorker Archiving 恢复完成：推进 Completed={CompletedCount} 条，标记 Failed={FailedCount} 条",
            completedCount, failedCount);
    }

    /// <summary>推进 Completed 并补一条中文时间线步骤</summary>
    private void CompleteWithStep(MediaItem item, string reason)
    {
        item.Transition(MediaItemStatus.Completed);
        item.AppendStep(MediaItemStatus.Completed, _clock.UtcNow, durMs: 0,
            SerializeStep(new { reason, target = item.TargetPath, recovery = true }));
    }

    /// <summary>标记 Failed 并补一条中文时间线步骤（与 ProcessFileService 的 Failed 步骤口径一致：reason + fromStage）</summary>
    private void MarkFailedWithStep(MediaItem item, string reason)
    {
        MediaItemStatus old = item.Status;
        item.MarkFailed(reason);
        item.AppendStep(MediaItemStatus.Failed, _clock.UtcNow, durMs: 0,
            SerializeStep(new { reason, fromStage = old.ToString(), recovery = true }));
    }

    /// <summary>发 media.failed Webhook（payload 口径对齐 ProcessFileService.EmitFailedAsync）；实现侧自吞异常</summary>
    private Task EmitFailedAsync(MediaItem item, string error, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaFailed, new
        {
            mediaItemId = item.Id,
            sourcePath = item.SourcePath,
            fileName = item.FileName,
            error,
        }, ct);

    /// <summary>按记录的 TMDB / 分类 / ParsedInfo 经去向预览（只读）重算预期归档落点；字段不全 / 预览失败返回 null</summary>
    /// <remarks>
    /// 复用 IReviewService.PreviewPathsAsync：与实际归档共用 ArchiveFolderResolver（TMDB 规范名 + {tmdb-NNN} 目录复用），
    /// 保证重算落点 = 崩溃前 ArchiveAsync 真实使用的落点。已知差异：预览侧不带文件解析篇章名（seasonTitle 回退），
    /// 该极端场景重算路径不命中 → 维持旧行为标 Failed（不劣化）。IReviewService 为 Scoped，经作用域解析。
    /// </remarks>
    private async Task<string?> TryResolveExpectedTargetAsync(MediaItem item, CancellationToken ct)
    {
        if (item.TmdbId is null || string.IsNullOrEmpty(item.TmdbMediaType) || item.CategoryId is null)
        {
            return null;
        }
        ParsedInfo? info = ParsedInfo.FromJson(item.ParsedInfo);
        if (info is null)
        {
            return null;
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IReviewService review = scope.ServiceProvider.GetRequiredService<IReviewService>();
            ReviewPreviewPathResult preview = await review.PreviewPathsAsync(new ReviewPreviewPathRequest(new[]
            {
                new ReviewPreviewPathItem(
                    Key: item.Id.ToString(),
                    TmdbId: item.TmdbId.Value,
                    MediaType: item.TmdbMediaType!,
                    Title: info.Title,
                    Year: info.Year,
                    Season: info.Season,
                    Episode: info.Episode,
                    EpisodeEnd: info.EpisodeEnd,
                    CategoryId: item.CategoryId.Value,
                    FileName: item.FileName),
            }), ct);

            ReviewPreviewPathEntry? entry = preview.Entries.FirstOrDefault();
            if (entry?.FullPath is null)
            {
                _logger.LogDebug("重算预期落点失败：MediaItemId={Id}，预览返回错误：{Error}", item.Id, entry?.Error ?? "(空结果)");
                return null;
            }
            return entry.FullPath;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重算预期落点异常（按未落地处理）：MediaItemId={Id}", item.Id);
            return null;
        }
    }

    /// <summary>容错取文件长度；取不到（瞬断 / 权限）返回 -1，调用方按「无法核对」处理</summary>
    private long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取目标文件长度失败（跳过长度核对）：{Path}", path);
            return -1;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 在途 Queued / 中途态重置
    // ──────────────────────────────────────────────────────────────

    /// <summary>把在途记录重置回 Queued 并落库，返回待重新入队清单（含按前缀推导的 WatchFolderId）</summary>
    private async Task<IReadOnlyList<PendingFileItem>> ResetInterruptedToQueuedAsync(CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        MediaItemStatus[] inflight =
        {
            MediaItemStatus.Detected,
            MediaItemStatus.Queued,
            MediaItemStatus.Parsing,
            MediaItemStatus.TmdbMatching,
            MediaItemStatus.AiParsing,
            MediaItemStatus.TmdbRematching,
            MediaItemStatus.Classifying,
        };

        List<MediaItem> items = await db.MediaItems
            .Where(m => inflight.Contains(m.Status))
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            _logger.LogDebug("未发现在途（Queued/中途态）记录，跳过启动重排");
            return Array.Empty<PendingFileItem>();
        }

        // 启用监控目录：按 SourcePath 前缀匹配推导 WatchFolderId（多层路径解析需要监控根）
        List<(long Id, string Path)> folders = (await db.WatchFolders
                .Where(f => f.Enabled)
                .Select(f => new { f.Id, f.Path })
                .ToListAsync(ct))
            .Select(f => (f.Id, f.Path))
            .ToList();

        List<PendingFileItem> result = new(items.Count);
        foreach (MediaItem item in items)
        {
            MediaItemStatus old = item.Status;
            item.RequeueInterrupted();
            // 时间线留痕：原 Queued 记录原地幂等也补一条（重启丢队列 + 重新入队本身就是一次干预）
            item.AppendStep(MediaItemStatus.Queued, _clock.UtcNow, durMs: 0,
                SerializeStep(new { reason = "启动恢复：进程重启丢失内存队列，重置回 Queued 重新入队", fromStage = old.ToString(), recovery = true }));
            long folderId = ResolveWatchFolderId(item.SourcePath, folders);
            result.Add(new PendingFileItem(item.SourcePath, folderId, PendingFileSource.FullScan));
        }

        await db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "启动重排：发现 {Count} 条在途记录（进程内队列重启丢失），已重置为 Queued 待重新入队", items.Count);
        return result;
    }

    /// <summary>按最长前缀匹配把 SourcePath 归属到某个启用监控目录；无匹配返回 0（退化为单层父目录解析）</summary>
    private static long ResolveWatchFolderId(string sourcePath, IReadOnlyList<(long Id, string Path)> folders)
    {
        long bestId = 0;
        int bestLen = -1;
        foreach ((long id, string path) in folders)
        {
            if (!string.IsNullOrEmpty(path)
                && sourcePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)
                && path.Length > bestLen)
            {
                bestId = id;
                bestLen = path.Length;
            }
        }
        return bestId;
    }

    /// <summary>后台把重排清单灌回内存队列；满则等待消费者腾位（不阻塞启动），关停时取消</summary>
    private async Task DrainEnqueueAsync(IReadOnlyList<PendingFileItem> items, CancellationToken ct)
    {
        int enqueued = 0;
        try
        {
            foreach (PendingFileItem item in items)
            {
                await _queue.EnqueueAsync(item, ct);
                enqueued++;
            }
            _logger.LogInformation("启动重排：{Count} 条在途记录已重新入队，等待 TaskProcessor 串行消化", enqueued);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("启动重排入队被取消（应用关停），已入队 {Count}/{Total}", enqueued, items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动重排入队异常，已入队 {Count}/{Total}", enqueued, items.Count);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 时间线序列化（与 ProcessFileService.StepJsonOptions 同口径）
    // ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string SerializeStep(object detail) => JsonSerializer.Serialize(detail, StepJsonOptions);
}
