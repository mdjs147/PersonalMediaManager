using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

/// <summary>归档服务（D7.4）— 路径计算 + 文件落地 + 元数据生成 + Webhook 出 Outbox</summary>
/// <remarks>
/// 流程（需求文档 §3.6 + §3.10）：
///   1. 加载 CategoryDefinition.TargetRoot；解析 MediaItem.ParsedInfo JSON 拿 title/year/season/episode
///   2. 按 type 用 PlexNamingConventions 算相对路径，拼出绝对路径
///   3. PathSafetyGuard 校验目标在分类根之下（防恶意标题路径穿越）
///   4. 同名冲突 → 按 Archive_ConflictPolicy 决策（Skip 跳过 / Overwrite 升级替换 / KeepBoth 多版本 / Ask 询问转待确认）；resolution=ForceOverwrite 调用参数可强制无条件覆盖
///   5. 归档前磁盘空间检查（不足 → BusinessException → Failed，清理后可批量重试）
///   6. IFileMover.MoveAsync 移视频
///   7. 扫字幕（视频同目录：stem 前缀 ∪ 剧集季集匹配；Subs 等字幕子目录：剧集季集匹配 ∪ 电影收编）
///      → SubtitleRenamer.Plan → 逐个移
///   8. MetadataFinalizer 写 nfo（电影 movie / 剧集 tvshow+episode）
///   9. 给所有 Enabled+订阅 media.archived 的 Webhook 写 Delivery 行 + EnqueueAsync 入 Outbox
///
/// 异常策略（以「视频是否已落地」为界）：
///   · 落地前（步骤 1~6：校验 / 命名 / 路径穿越 / 同名冲突 / 磁盘检查 / 移视频）任一失败 → 直接抛，
///     由 ProcessFileService catch 转 Failed；此时源文件未动，可安全 Rescan 重试。
///   · 落地后（步骤 7 字幕 / 步骤 8 nfo / 步骤 9 Webhook）失败 → 不抛，降级为 Warning 收集进
///     ArchiveResult.Warnings（标记「待补元数据」），Outcome 仍 Completed。否则源已删、Rescan 因源缺失
///     不可恢复、记录永久卡 Failed 与磁盘现实不符、nfo / 海报永不补写（网络盘核心场景真实可发生）；
///     与海报容错（TryCopyPosterAsync）策略统一。
///   · 取消透传必须带 when (ct.IsCancellationRequested) 过滤：HttpClient 超时（如海报兜底下载 30s 超时）
///     抛的 TaskCanceledException 是 OperationCanceledException 子类但 ct 并未取消（伪装取消），
///     不过滤会穿透降级信封被编排层当真取消转 Failed —— 视频已落地源已删的不可恢复毒状态。
///
/// 海报拷贝（需求文档 §3.6.4）：IPosterDownloader 契约已提到 Application/Contracts（解 §0.5 互引红线），
/// 归档时把缓存海报落地为 poster.jpg（电影同目录 / 剧集根目录）；缓存缺失则就地兜底下载，全程容错不阻断主归档。
/// fanart + season-poster 需 backdrop_path 与季级 poster（当前元数据缓存未存），留待后续扩展 TmdbMetadataCache 再追加。
/// </remarks>
internal sealed class ArchiveService : IArchiveService
{
    private const string ArchivedEvent = WebhookEvents.MediaArchived;
    private const string ConflictPolicyKey = "Archive_ConflictPolicy";
    private const string MinFreeSpaceKey = "Archive_MinFreeSpaceMB";
    /// <summary>Webhook 总开关 key（须与 SystemSettingConfig 种子 + WebhookSubscriptionService.GlobalEnabledKey 一致）</summary>
    private const string WebhookEnabledKey = "Webhook_Enabled";
    /// <summary>归档后清理源端空目录总开关 key（须与 SystemSettingConfig 种子一致）</summary>
    private const string CleanEmptyDirKey = "File.CleanEmptyDir";
    /// <summary>空目录清理可忽略扩展名清单 key（逗号分隔，须与 SystemSettingConfig 种子一致）</summary>
    private const string CleanEmptyDirIgnoreExtsKey = "File.CleanEmptyDirIgnoreExts";

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IFileMover _fileMover;
    private readonly IAudioRemuxer _audioRemuxer;
    private readonly IEmptyDirectoryCleaner _emptyDirCleaner;
    private readonly IWebhookOutboxQueue _outboxQueue;
    private readonly IClock _clock;
    private readonly IPosterDownloader _poster;
    private readonly AppPaths _paths;
    private readonly ILogger<ArchiveService> _logger;
    private readonly IAlertService? _alert;

    public ArchiveService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IFileMover fileMover,
        IAudioRemuxer audioRemuxer,
        IEmptyDirectoryCleaner emptyDirCleaner,
        IWebhookOutboxQueue outboxQueue,
        IClock clock,
        IPosterDownloader poster,
        AppPaths paths,
        ILogger<ArchiveService> logger,
        IAlertService? alert = null)
    {
        _dbFactory = dbFactory;
        _fileMover = fileMover;
        _audioRemuxer = audioRemuxer;
        _emptyDirCleaner = emptyDirCleaner;
        _outboxQueue = outboxQueue;
        _clock = clock;
        _poster = poster;
        _paths = paths;
        _logger = logger;
        _alert = alert;
    }

    public Task<ArchiveResult> ArchiveAsync(MediaItem item, CancellationToken ct = default)
        => ArchiveCoreAsync(item, ArchiveOperation.Move, null, ArchiveConflictResolution.FollowPolicy, ct);

    public Task<ArchiveResult> ArchiveAsync(MediaItem item, ArchiveOperation operation, CancellationToken ct = default)
        => ArchiveCoreAsync(item, operation, null, ArchiveConflictResolution.FollowPolicy, ct);

    public Task<ArchiveResult> ArchiveAsync(MediaItem item, ArchiveOperation operation, AudioRemuxPlan? remuxPlan, CancellationToken ct = default)
        => ArchiveCoreAsync(item, operation, remuxPlan, ArchiveConflictResolution.FollowPolicy, ct);

    public Task<ArchiveResult> ArchiveAsync(MediaItem item, ArchiveOperation operation, ArchiveConflictResolution resolution, CancellationToken ct = default)
        => ArchiveCoreAsync(item, operation, null, resolution, ct);

    private async Task<ArchiveResult> ArchiveCoreAsync(MediaItem item, ArchiveOperation operation, AudioRemuxPlan? remuxPlan, ArchiveConflictResolution resolution, CancellationToken ct)
    {
        // 1. 校验前置条件
        if (item.CategoryId is null)
            throw new BusinessException("MediaItem 缺少 CategoryId，无法归档");
        if (item.TmdbId is null || string.IsNullOrEmpty(item.TmdbMediaType))
            throw new BusinessException("MediaItem 缺少 TmdbId / TmdbMediaType，无法归档");
        if (string.IsNullOrEmpty(item.ParsedInfo))
            throw new BusinessException("MediaItem 缺少 ParsedInfo，无法归档");

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        CategoryDefinition? cat = await db.CategoryDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == item.CategoryId, ct);
        if (cat is null)
            throw new BusinessException($"分类不存在：CategoryId={item.CategoryId}");

        ParsedInfo info = ParseInfo(item.ParsedInfo);
        bool isTv = string.Equals(item.TmdbMediaType, "tv", StringComparison.OrdinalIgnoreCase);
        int tmdbId = item.TmdbId!.Value; // 前置校验已保证非空

        // 提前加载 TMDB 元数据缓存：标题段一律以 TMDB 规范名为准（同一 tmdbId 稳定唯一），
        // 缓存缺失 / 标题为空时回退解析名。供「文件夹命名 + nfo 写入」共用，避免二次查询。
        TmdbMetadataCache? meta = await db.TmdbMetadataCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TmdbId == tmdbId && c.MediaType == item.TmdbMediaType, ct);

        // 标题段规范化（修复同一作品散落多文件夹的根因）：自动处理用解析名、人工确认用候选名，
        // 同一 tmdbId 因标题字符串差异（中英 / 大小写 / 篇章副标题）被算成不同目标目录。
        // 标题语言优先级：TMDB 中文名 meta.Title → TMDB 原文名 meta.OriginalTitle → 文件解析名 info.Title。
        // 「优先中文，无中文回退媒体原文/英文」：zh-CN 详情偶发空中文名（冷门国漫/日漫多见）时先用 TMDB 权威原文，
        // 最后才退到文件解析名（杂质名）。年份维持解析值（年份漂移由 {tmdb-NNN} 复用闸兜底）。
        string title = ArchiveFolderResolver.FirstNonBlank(meta?.Title, meta?.OriginalTitle, info.Title)
            ?? throw new BusinessException("TMDB 规范标题与 ParsedInfo.title 均为空，无法按 Plex 命名");
        int year = info.Year ?? throw new BusinessException("ParsedInfo.year 不能为空，无法按 Plex 命名");
        string extension = Path.GetExtension(item.SourcePath);
        if (string.IsNullOrEmpty(extension))
            throw new BusinessException("源文件无扩展名，无法归档");

        // 2. 算目标相对路径：与去向预览 / 整理演练共用 ArchiveFolderResolver，杜绝「预览 ≠ 实际落点」。
        //    剧集根 / 电影目录按「{tmdb-NNN} 唯一性」解析：分类根下已存在同 tmdbId 标记目录则落入其中，
        //    即便本次标题段与历史目录不同（中英 / 大小写 / 篇章副标题）也不再新建第二个文件夹。
        int season = 0, episode = 0;
        int? seasonYear = null;
        string? seasonTitle = null;
        if (isTv)
        {
            season = info.Season ?? throw new BusinessException("剧集归档缺 season");
            episode = info.Episode ?? throw new BusinessException("剧集归档缺 episode");
            // 多季：从 TMDB 缓存原始响应取该季首播年 + 季名（缺失则季年份回退整剧 year、季标题仅看解析篇章名）
            (seasonYear, seasonTitle) = ArchiveFolderResolver.ResolveSeasonNaming(meta?.RawJson, info.SeasonTitle, season);
        }
        // 归档命名模板（用户可在「归档命名」设置页自定义；非法 / 缺失逐项回退 Plex 默认，绝不阻断归档）。
        // 与去向预览 / 整理演练共用同一渲染管道，模板穿过 BuildRelativeTargetPath 这一咽喉保证「预览 = 实际落点」。
        NamingTemplateOptions namingTemplate = await ArchiveNamingTemplateReader.ReadAsync(db, _logger, ct);
        string relativeTarget = ArchiveFolderResolver.BuildRelativeTargetPath(
            title, year, tmdbId, isTv, season, episode, info.EpisodeEnd, extension, cat.TargetRoot, _logger,
            seasonYear, seasonTitle, namingTemplate, meta?.OriginalTitle);
        string absoluteTarget = Path.Combine(cat.TargetRoot, relativeTarget);

        // 3. 路径穿越守护
        PathSafetyGuard.EnsureWithinRoot(cat.TargetRoot, absoluteTarget);

        // 3.5 源已在规范目标位（典型：孤儿文件重新入库——文件本就在分类目录下，解析出的规范路径恰好等于其自身）：
        //     视为「已就位」直接登记成功，绝不移动、也不进同名冲突分支（否则会把文件搬到自己头上或被自冲突 Skip）。
        //     正常归档源在监控/下载目录，与目标必不相等，此分支只对「孤儿认领」生效，对常规流程零影响。
        if (string.Equals(Path.GetFullPath(item.SourcePath), Path.GetFullPath(absoluteTarget), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("源文件已在规范目标位，登记为已归档（不移动）：{Target}", absoluteTarget);
            return new ArchiveResult(absoluteTarget, ArchiveOutcome.Completed);
        }

        // 4. 同名冲突：人工裁定覆盖（ForceOverwrite）优先于系统策略；否则按 Archive_ConflictPolicy 决策
        //    （Skip 跳过 / Overwrite 升级替换 / KeepBoth 保留多版本 / Ask 询问→转待确认人工裁定）
        if (File.Exists(absoluteTarget))
        {
            if (resolution == ArchiveConflictResolution.ForceOverwrite)
            {
                // 人工在审核页裁定覆盖：无条件删旧 + 清旧伴生 + 失效旧记录，继续落新文件（不比新旧大小）
                _logger.LogInformation("人工裁定覆盖：删除已存在文件并替换：{Target}", absoluteTarget);
                await OverwriteExistingAsync(db, absoluteTarget, item.Id, ct);
            }
            else
            {
                ArchiveConflictPolicy policy = await ReadConflictPolicyAsync(db, ct);
                switch (policy)
                {
                    case ArchiveConflictPolicy.Overwrite:
                        // 升级替换：仅当新文件更大（更高清）才替换，否则保留已有并跳过，避免低质量覆盖高质量
                        long existingSize = new FileInfo(absoluteTarget).Length;
                        long incomingSize = new FileInfo(item.SourcePath).Length;
                        if (incomingSize <= existingSize)
                        {
                            _logger.LogInformation("升级替换：新文件不大于已存在文件，跳过：{Target}", absoluteTarget);
                            return new ArchiveResult(absoluteTarget, ArchiveOutcome.ConflictSkipped);
                        }
                        _logger.LogInformation("升级替换：删除较小的已存在文件并替换：{Target}（{Old}→{New} 字节）",
                            absoluteTarget, existingSize, incomingSize);
                        await OverwriteExistingAsync(db, absoluteTarget, item.Id, ct);
                        break;

                    case ArchiveConflictPolicy.KeepBoth:
                        absoluteTarget = ResolveNonConflictingPath(absoluteTarget);
                        _logger.LogInformation("保留多版本：改用不冲突路径：{Target}", absoluteTarget);
                        break;

                    case ArchiveConflictPolicy.Ask:
                        // 询问：不做任何文件操作，返回 ConflictPending，由编排层转入待确认队列人工裁定是否覆盖
                        _logger.LogInformation("归档同名冲突（询问策略）→ 待人工确认是否覆盖：{Target}", absoluteTarget);
                        return new ArchiveResult(absoluteTarget, ArchiveOutcome.ConflictPending);

                    default: // Skip：不覆盖已存在文件
                        _logger.LogWarning("归档同名冲突，跳过：{Target}", absoluteTarget);
                        return new ArchiveResult(absoluteTarget, ArchiveOutcome.ConflictSkipped);
                }
            }
        }

        // 5. 归档前磁盘空间检查（不足 → BusinessException → ProcessFileService 转 Failed，清理后可批量重试）
        await EnsureEnoughDiskSpaceAsync(item.SourcePath, absoluteTarget, db, ct);

        // 6. 移视频：remuxPlan 非 null → ffmpeg 流复制丢不兼容音轨直接输出到目标（就近目标盘，省一次大文件跨卷重写），
        //    Move 语义重混后删源 / Copy 语义保留源；否则按 operation 纯文件移动 / 复制。
        if (remuxPlan is not null)
            await _audioRemuxer.RemuxAsync(
                remuxPlan.FfmpegPath, item.SourcePath, absoluteTarget,
                remuxPlan.DropStreamIndexes, deleteSourceAfter: operation == ArchiveOperation.Move, ct);
        else if (operation == ArchiveOperation.Copy)
            await _fileMover.CopyAsync(item.SourcePath, absoluteTarget, ct);
        else
            await _fileMover.MoveAsync(item.SourcePath, absoluteTarget, ct);

        // 视频已落地 = 归档主体成功。以下字幕（步骤7）/ nfo（步骤8）/ Webhook（步骤9）均降级容错：失败仅收集进
        // warnings 标记「待补元数据」，绝不整条判 Failed —— 否则源已删、Rescan 因源缺失不可恢复，记录永久卡
        // Failed 与磁盘现实不符，nfo / 海报永不补写。与海报容错（TryCopyPosterAsync）策略统一。
        // 取消上抛一律带 when (ct.IsCancellationRequested) 过滤：HttpClient 超时等抛出的 TaskCanceledException
        // 是 OperationCanceledException 子类但 ct 并未取消（伪装取消），不过滤会被编排层当真取消转 Failed。
        List<string> warnings = new();

        // 7. 字幕同步（跟随主文件 operation；整体纳入落地后降级信封：枚举源目录抛异常——网络盘抖动 /
        //    源目录被并发清理——或单条移动失败均收 Warning，不再让已落地的归档整条判 Failed）
        // 字幕等伴生文件归属匹配用「源文件名命名空间」的季集：剧集组（episode_group）翻译会把编组内集号改写成
        // 正典 SxxEyy，但磁盘上的视频 / 字幕文件名仍是编组内原始集号，必须用 Original*（翻译前原始季集）匹配，
        // 否则非同名字幕 / 字幕子目录里的字幕匹配不上被遗留源目录。落地重命名仍以正典命名的 absoluteTarget 为准
        // （见 TryMoveSubtitlesAsync 的 targetStem）。普通路径 Original* 全为 null → 回退正典，行为与修复前一致。
        int matchSeason = info.OriginalSeason ?? season;
        int matchEpisode = info.OriginalEpisode ?? episode;
        int matchEpisodeEnd = info.OriginalEpisode is not null
            ? info.OriginalEpisodeEnd ?? info.OriginalEpisode.Value   // 剧集组：原始末集（单集时 = 原始集号）
            : info.EpisodeEnd ?? episode;                             // 普通路径：保持既有正典末集回退
        try
        {
            await TryMoveSubtitlesAsync(item.SourcePath, absoluteTarget, operation,
                isTv, matchSeason, matchEpisode, matchEpisodeEnd, warnings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "字幕处理失败（视频已落地，跳过字幕同步）：{Source} → {Target}", item.SourcePath, absoluteTarget);
            warnings.Add($"字幕处理失败：{ex.Message}");
        }

        // 8. nfo（容错：失败仅记 Warning + 标记待补，不阻断已落地的归档主体）
        try
        {
            await WriteNfoAsync(item, info, isTv, title, year, absoluteTarget, meta, warnings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "归档元数据(nfo)写入失败，视频已落地，标记待补元数据：{Target}", absoluteTarget);
            warnings.Add($"元数据(nfo)写入失败：{ex.Message}");
        }

        // 9. 发 Webhook 入队（容错：DB 锁 / 写入失败仅记 Warning，不阻断已落地的归档主体；
        //    注意 Delivery 行未写成则该事件已彻底丢失——没有任何补发机制，失败只能收进 Warnings 供用户感知）
        try
        {
            await EnqueueArchivedWebhooksAsync(item, absoluteTarget, isTv, title, year, warnings, db, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "归档 Webhook 入队失败，视频已落地（该事件已丢失，无补发机制）：{Target}", absoluteTarget);
            warnings.Add($"Webhook 入队失败：{ex.Message}");
        }

        // 10. 清理源端空目录（File.CleanEmptyDir=true 时；仅 Move —— Copy 源文件仍在，目录自然非空不会被删）。
        //     落地后降级容错：清理失败仅记 Warning，绝不回滚已落地的归档主体。
        if (operation == ArchiveOperation.Move)
        {
            try
            {
                await TryCleanEmptySourceDirAsync(item.SourcePath, db, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "源端空目录清理失败（视频已落地，跳过清理）：{Source}", item.SourcePath);
                warnings.Add($"源端空目录清理失败：{ex.Message}");
            }
        }

        if (warnings.Count > 0)
            _logger.LogWarning("归档完成但元数据待补（{Count} 项）：{Source} → {Target}",
                warnings.Count, item.SourcePath, absoluteTarget);
        else
            _logger.LogInformation("归档完成：{Source} → {Target}", item.SourcePath, absoluteTarget);

        return new ArchiveResult(absoluteTarget, ArchiveOutcome.Completed, warnings);
    }

    // ---------- 源端空目录清理 ----------

    /// <summary>读 File.CleanEmptyDir 开关 + 可忽略扩展名清单，命中则从源文件目录向上回收空目录</summary>
    /// <remarks>
    /// 开关关闭直接返回。可忽略清单（File.CleanEmptyDirIgnoreExts，逗号 / 分号 / 空白分隔）默认 .torrent：
    /// 目录仅剩这些扩展名残留 + 子目录递归为空即视为空，连残留一并删；清单清空则退化为「仅真正的空目录」。
    /// 上溯下界 = 源路径所属的最深监控根（WatchFolder.Path），绝不删除监控根本身；源不在任何监控根下则跳过。
    /// </remarks>
    private async Task TryCleanEmptySourceDirAsync(string sourcePath, PmmDbContext db, CancellationToken ct)
    {
        // 一次查询取回两项设置
        Dictionary<string, string?> settings = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == CleanEmptyDirKey || s.Key == CleanEmptyDirIgnoreExtsKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        settings.TryGetValue(CleanEmptyDirKey, out string? enabledRaw);
        if (!string.Equals(enabledRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return; // 开关关闭（缺失 / 非 "true" 一律视为关）

        string? sourceDir = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(sourceDir))
            return;

        // 定位最深监控根：Path 为 sourceDir 祖先（或自身）且路径最长者
        List<string> roots = await db.WatchFolders.AsNoTracking()
            .Select(w => w.Path)
            .ToListAsync(ct);
        string? boundary = FindDeepestRoot(sourceDir, roots);
        if (boundary is null)
        {
            _logger.LogDebug("源路径不在任何监控根之下，跳过空目录清理：{Source}", sourcePath);
            return;
        }

        settings.TryGetValue(CleanEmptyDirIgnoreExtsKey, out string? ignoreRaw);
        IReadOnlySet<string> ignoreExts = ParseIgnoreExtensions(ignoreRaw);

        IReadOnlyList<string> deleted = _emptyDirCleaner.CleanUpward(sourceDir, boundary, ignoreExts, ct);
        if (deleted.Count > 0)
            _logger.LogInformation("归档后回收源端空目录 {Count} 个（根 {Boundary}）：{Source}",
                deleted.Count, boundary, sourcePath);
    }

    /// <summary>在监控根列表里找 sourceDir 所属的最深根（Path 为 sourceDir 祖先或自身、规范化后路径最长）</summary>
    /// <remarks>嵌套监控根时返回最深者，确保上溯回收绝不越过内层根爬到外层根。</remarks>
    private static string? FindDeepestRoot(string sourceDir, IReadOnlyList<string> roots)
    {
        string normSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDir));
        string? best = null;
        int bestLen = -1;
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            bool isAncestorOrSelf = string.Equals(normSource, normRoot, StringComparison.OrdinalIgnoreCase)
                || normSource.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (isAncestorOrSelf && normRoot.Length > bestLen)
            {
                best = normRoot;
                bestLen = normRoot.Length;
            }
        }
        return best;
    }

    /// <summary>解析可忽略扩展名清单（逗号 / 分号 / 空白分隔），规范化为带前导点；空 → 空集（严格模式）</summary>
    private static IReadOnlySet<string> ParseIgnoreExtensions(string? raw)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (string token in raw.Split([',', ';', '\n', '\r', '\t', ' '],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(token.StartsWith('.') ? token : "." + token);
        }
        return set;
    }

    // ---------- 字幕 ----------

    private async Task TryMoveSubtitlesAsync(
        string sourceVideoPath, string targetVideoPath, ArchiveOperation operation,
        bool isTv, int season, int episode, int episodeEnd, List<string> warnings, CancellationToken ct)
    {
        string? sourceDir = Path.GetDirectoryName(sourceVideoPath);
        if (sourceDir is null || !Directory.Exists(sourceDir)) return;

        IReadOnlyList<SubtitleCandidate> candidates =
            EnumerateSubtitles(sourceDir, sourceVideoPath, isTv, season, episode, episodeEnd);
        if (candidates.Count == 0) return;

        string targetDir = Path.GetDirectoryName(targetVideoPath)!;
        string targetStem = Path.GetFileNameWithoutExtension(targetVideoPath);
        IReadOnlyList<SubtitleRenameDecision> plan = SubtitleRenamer.Plan(targetStem, candidates);

        foreach (SubtitleRenameDecision d in plan)
        {
            if (d.Action != SubtitleAction.Rename)
            {
                _logger.LogInformation("字幕跳过：{File}（{Reason}）", d.Source.FileName, d.Reason);
                continue;
            }
            string subTarget = Path.Combine(targetDir, d.TargetFileName!);
            try
            {
                if (operation == ArchiveOperation.Copy)
                    await _fileMover.CopyAsync(d.Source.FullPath, subTarget, ct);
                else
                    await _fileMover.MoveAsync(d.Source.FullPath, subTarget, ct);
                _logger.LogDebug("字幕同步：{Src} → {Dst}", d.Source.FullPath, subTarget);
            }
            catch (FileConflictException)
            {
                _logger.LogWarning("字幕目标已存在，跳过：{Dst}", subTarget);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 真取消透传（伪装取消的超时类 OCE 落入下方通用分支收 Warning，不中断剩余字幕）
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "字幕移动失败（跳过单条）：{Src}", d.Source.FullPath);
                warnings.Add($"字幕处理失败：{d.Source.FileName}：{ex.Message}");
            }
        }
    }

    /// <summary>字幕专用子目录名（视频同目录的直接子目录里只认这些名字，防止把整个源树当字幕库乱扫）</summary>
    private static readonly HashSet<string> SubtitleDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Subs", "Subtitles", "Sub", "字幕",
    };

    /// <summary>限深递归枚举选项（字幕子目录扫描 + 电影守门的整树视频扫描共用；忽略无权限子目录防 SMB 抖动中断）</summary>
    private static readonly EnumerationOptions LimitedRecursionEnumeration = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 3,
        IgnoreInaccessible = true,
    };

    /// <summary>发现归属本视频的字幕候选（需求文档 §3.6.3）</summary>
    /// <remarks>
    /// 两路扫描，归属规则按位置区分：
    /// · 视频同目录顶层：stem 前缀 + 边界（同名字幕，电影剧集通用）∪ 剧集季集匹配（不同名字幕，
    ///   如「[ANi] 芙莉莲 - 01.mkv」配「Frieren.S01E01.chs.srt」）。电影顶层不做不同名收编——
    ///   「Movie 2.CHS.srt」与「无关字幕」机器不可区分，历史上已有两例过抓防护测试钉死此边界。
    /// · 字幕子目录（Subs/Subtitles/Sub/字幕，含嵌套限深 3）：剧集按季集匹配（段+文件名合并提取，
    ///   覆盖「Subs/S2/EP01.srt」「Subs/EP01/chs.srt」拆分结构）∪ 电影收编无季集标记的字幕——
    ///   位置即归属信号，但须守门「源目录树内无其它视频」，防平铺多部电影共享 Subs 时互抢。
    ///   守门注意：Move 模式下先归档的视频已移走，最后一部电影可能收走前面剩下的无主字幕——
    ///   平铺多电影共享 Subs 属病态交付结构，容忍该行为（日志可溯）。
    /// 带季集标记但匹配不上的字幕一律留在原地（剧集邻集字幕 / 电影目录里混入的剧集字幕）。
    /// </remarks>
    private static IReadOnlyList<SubtitleCandidate> EnumerateSubtitles(
        string sourceDir, string sourceVideoPath, bool isTv, int season, int episode, int episodeEnd)
    {
        string sourceStem = Path.GetFileNameWithoutExtension(sourceVideoPath);
        List<SubtitleCandidate> list = new();

        // 一、视频同目录顶层
        foreach (string path in Directory.EnumerateFiles(sourceDir))
        {
            string fileName = Path.GetFileName(path);
            if (!IsSubtitleLike(fileName)) continue;
            string fileStem = Path.GetFileNameWithoutExtension(fileName);
            bool owns = MatchesStemPrefix(fileStem, sourceStem);
            if (!owns && isTv)
            {
                (int? s, int? e) = SubtitleRenamer.DetectSeasonEpisode(fileStem);
                owns = SubtitleRenamer.MatchesEpisode(s, e, season, episode, episodeEnd);
            }
            if (!owns) continue;
            list.Add(new SubtitleCandidate(path, fileName, new FileInfo(path).Length));
        }

        // 二、字幕专用子目录（仅视频同目录的直接子目录）
        // 电影收编守门懒求值：只有存在字幕子目录时才付出整树扫视频的代价
        bool? movieFallbackAllowed = null;
        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            if (!SubtitleDirNames.Contains(Path.GetFileName(subDir))) continue;
            foreach (string path in Directory.EnumerateFiles(subDir, "*", LimitedRecursionEnumeration))
            {
                string fileName = Path.GetFileName(path);
                if (!IsSubtitleLike(fileName)) continue;

                // 中间目录段（相对源目录，如 Subs/简体 → ["Subs","简体"]）：参与季集提取与语言回退
                string relDir = Path.GetRelativePath(sourceDir, Path.GetDirectoryName(path)!);
                string[] segments = relDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fileStem = Path.GetFileNameWithoutExtension(fileName);

                // 文件名优先、目录段从深到浅合并提取（季在目录段、集在文件名的拆分结构可拼齐）
                List<string> texts = new(segments.Length + 1) { fileStem };
                for (int i = segments.Length - 1; i >= 0; i--) texts.Add(segments[i]);
                (int? s, int? e) = SubtitleRenamer.DetectSeasonEpisode(texts);

                bool owns;
                if (isTv)
                {
                    owns = SubtitleRenamer.MatchesEpisode(s, e, season, episode, episodeEnd);
                }
                else
                {
                    // 电影：字幕带任何季集标记说明是剧集字幕，不收；无标记 + 树内唯一视频 → 收编
                    movieFallbackAllowed ??= !HasOtherVideoInTree(sourceDir, sourceVideoPath);
                    owns = s is null && e is null && movieFallbackAllowed.Value;
                }
                if (!owns) continue;

                string hint = string.Join('/', segments);
                list.Add(new SubtitleCandidate(path, fileName, new FileInfo(path).Length, hint));
            }
        }
        return list;
    }

    /// <summary>扩展名是受支持字幕或字幕压缩包（压缩包仍入候选，由 SubtitleRenamer 标 Skip 记日志）</summary>
    private static bool IsSubtitleLike(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return SubtitleRenamer.SupportedExtensions.Contains(ext)
            || SubtitleRenamer.ArchiveExtensions.Contains(ext);
    }

    /// <summary>同名字幕判定：以源 stem 为前缀且边界字符为 . - _ 之一（或完全同名）</summary>
    /// <remarks>
    /// 纯前缀会过抓：「EP1.mkv」抓走「EP11.chs.srt」、「Movie.mkv」抓走「Movie 2.chs.srt」。
    /// 空格不视为边界——空格后通常是标题延续（如「Movie 2」属另一部作品），而非语言后缀分隔。
    /// </remarks>
    private static bool MatchesStemPrefix(string fileStem, string sourceStem)
    {
        if (!fileStem.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase)) return false;
        return fileStem.Length == sourceStem.Length
            || fileStem[sourceStem.Length] is '.' or '-' or '_';
    }

    /// <summary>源目录树内（含子目录，限深）是否存在本视频之外的其它视频文件</summary>
    /// <remarks>电影收编 Subs 字幕的守门：有其它视频说明源目录不是单部电影的交付单元，收编有互抢风险。</remarks>
    private static bool HasOtherVideoInTree(string sourceDir, string sourceVideoPath)
    {
        string self = Path.GetFullPath(sourceVideoPath);
        foreach (string path in Directory.EnumerateFiles(sourceDir, "*", LimitedRecursionEnumeration))
        {
            if (!MediaFileExtensions.IsVideo(path)) continue;
            if (string.Equals(Path.GetFullPath(path), self, StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        return false;
    }

    // ---------- 升级替换（Overwrite） ----------

    /// <summary>覆盖已存在目标：删旧视频 + 清同 stem 旧伴生（字幕 / nfo）+ 失效仍指向该路径的旧 Completed 记录</summary>
    /// <remarks>升级替换（Overwrite，已比过新旧大小）与人工裁定覆盖（ForceOverwrite，不比大小）共用此清理序列，保证两条覆盖路径行为一致。</remarks>
    private async Task OverwriteExistingAsync(PmmDbContext db, string absoluteTarget, long currentItemId, CancellationToken ct)
    {
        File.Delete(absoluteTarget);
        // 旧视频已删：同步清理目标位同 stem 的旧字幕与旧 nfo —— 不清理则旧字幕占住目标名，
        // 新字幕全部同名冲突被跳过，新视频会永久配上旧时间轴字幕（覆盖后必然错轴）。
        DeleteReplacedCompanionFiles(absoluteTarget);
        // 旧文件已物理删除：失效其它仍指向该目标路径的「已完成」记录 —— 不失效则用户对旧记录
        // 撤销归档会把新文件搬走改名（丢片），内容去重也会把新文件误判为旧记录的有效副本。
        await InvalidateReplacedRecordsAsync(db, absoluteTarget, currentItemId, ct);
    }

    /// <summary>升级替换删除旧视频后，同步清理目标位同 stem 的旧字幕与旧 nfo</summary>
    /// <remarks>
    /// 仅删「{stem}.xxx」形态且扩展名为字幕 / .nfo 的伴生文件（SubtitleRenamer 产出的字幕与本服务产出的
    /// nfo 均为该形态）；「{stem} (1).mkv」等 KeepBoth 版本、同季其它集、poster.jpg、tvshow.nfo 均不以
    /// 「{stem}.」开头（或扩展名不符），不受影响。任何清理失败仅记日志不阻断（残留旧字幕会在新字幕落地时
    /// 按同名冲突跳过，与修复前行为一致，不会更糟）。
    /// </remarks>
    private void DeleteReplacedCompanionFiles(string targetVideoPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(targetVideoPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string stemPrefix = Path.GetFileNameWithoutExtension(targetVideoPath) + ".";
            foreach (string path in Directory.EnumerateFiles(dir))
            {
                string fileName = Path.GetFileName(path);
                if (!fileName.StartsWith(stemPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(fileName);
                bool isCompanion = string.Equals(ext, ".nfo", StringComparison.OrdinalIgnoreCase)
                    || SubtitleRenamer.SupportedExtensions.Contains(ext);
                if (!isCompanion) continue;
                try
                {
                    File.Delete(path);
                    _logger.LogInformation("升级替换：清理旧伴生文件：{Path}", path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "升级替换：旧伴生文件清理失败（跳过）：{Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "升级替换：旧伴生文件枚举失败（跳过清理）：{Target}", targetVideoPath);
        }
    }

    /// <summary>升级替换后失效其它仍指向同一目标路径的已完成记录（其归档副本已被物理删除）</summary>
    /// <remarks>
    /// 复用聚合既有回退出口 UndoArchive（Completed → Skipped，清 TargetPath / ArchivedAt / FileMissing）：
    /// Skipped 语义即「未归档入库」，与「归档副本已被升级替换删除」吻合，不新增任何状态枚举值。
    /// 失效后：① 撤销归档 / 退回重改因状态非 Completed（且缺 TargetPath）被安全拒绝，不会把新文件搬走改名；
    /// ② 内容去重只认 Completed 记录，不再把新文件误判为旧记录的有效副本。时间线追加中文说明供历史页溯源。
    /// </remarks>
    private async Task InvalidateReplacedRecordsAsync(PmmDbContext db, string targetPath, long currentItemId, CancellationToken ct)
    {
        List<MediaItem> replaced = await db.MediaItems
            .Where(m => m.TargetPath == targetPath
                     && m.Status == MediaItemStatus.Completed
                     && m.Id != currentItemId)
            .ToListAsync(ct);
        if (replaced.Count == 0) return;

        foreach (MediaItem old in replaced)
        {
            old.UndoArchive(); // 聚合既有「归档副本已不在」出口：Completed → Skipped，清 TargetPath / ArchivedAt
            old.AppendStep(MediaItemStatus.Skipped, _clock.UtcNow, durMs: 0, JsonSerializer.Serialize(new
            {
                reason = $"目标文件已被记录 #{currentItemId} 的新归档替换（升级替换），本记录的归档副本已删除",
                replacedBy = currentItemId,
                oldTargetPath = targetPath,
            }, StepJsonOptions));
            _logger.LogInformation("升级替换：记录 #{OldId} 的归档副本已被记录 #{NewId} 替换，标记失效（Completed → Skipped）",
                old.Id, currentItemId);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>时间线步骤 detail 序列化选项（camelCase + 保留中文原样，与 ProcessFileService 一致）</summary>
    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------- nfo ----------

    private async Task WriteNfoAsync(
        MediaItem item, ParsedInfo info, bool isTv, string title, int year,
        string absoluteTarget, TmdbMetadataCache? meta, List<string> warnings, CancellationToken ct)
    {
        string targetDir = Path.GetDirectoryName(absoluteTarget)!;
        string targetStem = Path.GetFileNameWithoutExtension(absoluteTarget);
        string originCountry = JoinFirst(meta?.OriginCountry);
        IReadOnlyList<string>? genres = ExtractGenreNames(meta?.Genres);

        string posterDir; // 海报落地目录：电影 = 视频同目录；剧集 = 剧集根目录（Plex/Kodi/Emby 通用 poster.jpg 约定）
        if (!isTv)
        {
            string nfoPath = Path.Combine(targetDir, $"{targetStem}.nfo");
            await MetadataFinalizer.WriteMovieNfoAsync(nfoPath, new MovieNfoData(
                TmdbId: item.TmdbId!.Value,
                Title: title,
                OriginalTitle: meta?.OriginalTitle,
                Year: year,
                Plot: meta?.Overview,
                OriginCountry: originCountry,
                Genres: genres), ct);
            posterDir = targetDir;
        }
        else
        {
            // 剧集：tvshow.nfo（剧集根目录）+ episode.nfo（与视频同目录）
            string showRoot = Path.GetDirectoryName(targetDir)!; // {root}/{tvFolder}（targetDir 是 .../Season NN）
            string tvShowNfoPath = Path.Combine(showRoot, "tvshow.nfo");
            if (!File.Exists(tvShowNfoPath))
            {
                await MetadataFinalizer.WriteTvShowNfoAsync(tvShowNfoPath, new TvShowNfoData(
                    TmdbId: item.TmdbId!.Value,
                    Title: title,
                    OriginalTitle: meta?.OriginalTitle,
                    Year: year,
                    TotalSeasons: meta?.TotalSeasons,
                    Plot: meta?.Overview,
                    OriginCountry: originCountry,
                    Genres: genres), ct);
            }

            string episodeNfoPath = Path.Combine(targetDir, $"{targetStem}.nfo");
            await MetadataFinalizer.WriteEpisodeNfoAsync(episodeNfoPath, new EpisodeNfoData(
                Season: info.Season!.Value,
                Episode: info.Episode!.Value,
                Title: null, // 单集标题暂时空（需 TmdbClient.GetEpisodeDetails，超出 D.7.4 范围）
                Plot: null,
                ShowTmdbId: item.TmdbId), ct);
            posterDir = showRoot;
        }

        // 海报：容错拷贝 poster.jpg（失败不阻断主归档；海报锦上添花，nfo 才是核心）
        await TryCopyPosterAsync(item.TmdbId!.Value, meta?.PosterPath, Path.Combine(posterDir, "poster.jpg"), warnings, ct);
    }

    /// <summary>容错拷贝海报到归档目录的 poster.jpg：缓存有则直接拷，缺失但有 PosterPath 则先下载兜底</summary>
    /// <remarks>
    /// 海报非归档核心步骤：任何失败仅收 Warning，不让海报问题把整条归档判 Failed。
    /// 缓存路径 {AppPaths.PostersDir}/{TmdbId}.jpg 通常已由 TmdbSearchService.GetDetailsAsync 下载；
    /// 此处兜底下载覆盖「详情阶段下载失败、归档时网络已恢复」场景，DownloadAsync 自身按文件存在幂等。
    /// 仅「真取消」（ct 已请求取消）上抛：海报下载 HttpClient 30s 超时抛的 TaskCanceledException 是
    /// OperationCanceledException 子类但 ct 未取消（伪装取消），必须落入降级分支收 Warning，
    /// 否则穿透为取消被编排层判 Failed —— 视频已落地源已删的不可恢复毒状态。
    /// </remarks>
    private async Task TryCopyPosterAsync(int tmdbId, string? posterPath, string targetPosterPath, List<string> warnings, CancellationToken ct)
    {
        try
        {
            string cached = Path.Combine(_paths.PostersDir, $"{tmdbId}.jpg");
            if (!File.Exists(cached))
            {
                if (string.IsNullOrWhiteSpace(posterPath)) return; // 无图源可下载，跳过
                await _poster.DownloadAsync(tmdbId, posterPath, _paths.CacheDir, ct);
            }
            if (File.Exists(cached))
                await MetadataFinalizer.CopyPosterAsync(cached, targetPosterPath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "海报归档拷贝失败（不影响主归档）：TmdbId={TmdbId} → {Target}", tmdbId, targetPosterPath);
            warnings.Add($"海报落地失败：{ex.Message}");
        }
    }

    // ---------- Webhook ----------

    private async Task EnqueueArchivedWebhooksAsync(
        MediaItem item, string absoluteTarget, bool isTv, string title, int year,
        IReadOnlyList<string> warnings, PmmDbContext db, CancellationToken ct)
    {
        // Webhook 总开关（默认关）：关闭时不查订阅、不产生任何 Webhook_Delivery、不入队
        if (!await IsWebhookEnabledAsync(db, ct))
            return;

        List<WebhookSubscription> subs = await db.WebhookSubscriptions
            .Where(s => s.Enabled)
            .ToListAsync(ct);

        if (subs.Count == 0) return;

        DateTimeOffset now = _clock.UtcNow;
        string requestId = Guid.NewGuid().ToString("N");
        string payload = JsonSerializer.Serialize(new
        {
            @event = ArchivedEvent,
            requestId,
            occurredAt = now,
            data = new
            {
                mediaItemId = item.Id,
                sourcePath = item.SourcePath,
                targetPath = absoluteTarget,
                tmdbId = item.TmdbId,
                type = isTv ? "tv" : "movie",
                title,
                year,
                categoryId = item.CategoryId,
                // 降级标记（向后兼容的新增字段）：metadataPending=true 表示视频已落地但字幕 / nfo / 海报
                // 存在待补项，warnings 为具体失败明细 —— 订阅端（如 Plex 刷库自动化）可据此延迟刷新或提醒补元数据
                metadataPending = warnings.Count > 0,
                warnings,
            },
        });

        foreach (WebhookSubscription sub in subs)
        {
            if (!(sub.Events?.Contains(ArchivedEvent) ?? false)) continue;
            WebhookDelivery d = new()
            {
                SubscriptionId = sub.Id,
                Event = ArchivedEvent,
                Payload = payload,
                Status = WebhookDeliveryStatus.Pending,
                Attempts = 0,
                RequestId = requestId,
            };
            db.WebhookDeliveries.Add(d);
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            // 入队（SaveChanges 后 Id 已填，从 ChangeTracker.Local 按 (SubscriptionId, RequestId) 重新定位）
            foreach (WebhookSubscription sub in subs)
            {
                if (!(sub.Events?.Contains(ArchivedEvent) ?? false)) continue;
                long deliveryId = db.WebhookDeliveries.Local
                    .Where(d => d.SubscriptionId == sub.Id && d.RequestId == requestId)
                    .Select(d => d.Id).Single();
                await _outboxQueue.EnqueueAsync(deliveryId, ct);
            }
        }
    }

    // ---------- helpers ----------

    /// <summary>读 Webhook 总开关（默认 false：缺失 / 非 "true" 一律视为关闭）</summary>
    private static async Task<bool> IsWebhookEnabledAsync(PmmDbContext db, CancellationToken ct)
    {
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == WebhookEnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读归档同名冲突策略（无值 / 非法值默认 Skip）</summary>
    private async Task<ArchiveConflictPolicy> ReadConflictPolicyAsync(PmmDbContext db, CancellationToken ct)
    {
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == ConflictPolicyKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return Enum.TryParse(raw, ignoreCase: true, out ArchiveConflictPolicy p) ? p : ArchiveConflictPolicy.Skip;
    }

    /// <summary>归档前磁盘空间检查：目标盘可用空间须 ≥ 源大小 + 余量（Archive_MinFreeSpaceMB）</summary>
    /// <remarks>
    /// 不足 → 抛 BusinessException 由 ProcessFileService 转 Failed（用户清理磁盘后可一键批量重试）。
    /// 网络盘 / UNC / 未就绪盘 DriveInfo 取不到容量 → 跳过检查（容错，不阻断归档）。
    /// </remarks>
    private async Task EnsureEnoughDiskSpaceAsync(string sourcePath, string targetPath, PmmDbContext db, CancellationToken ct)
    {
        long minFreeBytes = 0;
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == MinFreeSpaceKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (long.TryParse(raw, out long mb) && mb > 0)
        {
            // 读侧钳制防溢出：极端大值（如直接改库塞 long 上限）乘 1024*1024 会回绕成负数，使磁盘预检恒通过
            // 形同虚设；钳到 [0, 1_000_000_000] MB（≈953 TB，远超实际且乘法绝不溢出）。写侧范围校验由设置服务负责。
            minFreeBytes = Math.Clamp(mb, 0L, 1_000_000_000L) * 1024L * 1024L;
        }

        long required;
        try
        {
            required = new FileInfo(sourcePath).Length + minFreeBytes;
        }
        catch
        {
            return; // 源文件信息取不到，交由后续 MoveAsync 报错
        }

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root)) return;
            DriveInfo drive = new(root);
            if (!drive.IsReady) return;
            long available = drive.AvailableFreeSpace;
            if (available < required)
            {
                // 磁盘不足导致本次归档失败 → 主动告警（按盘根抑制，避免批量失败时同盘风暴）；告警失败不掩盖原始异常
                if (_alert is not null)
                    await _alert.RaiseAsync(
                        $"{WebhookEvents.DiskLow}:{root}",
                        WebhookEvents.DiskLow,
                        new { drive = root, availableMb = available / 1024 / 1024, requiredMb = required / 1024 / 1024, sourcePath },
                        ct);
                throw new BusinessException(
                    $"目标磁盘空间不足：可用 {available / 1024 / 1024} MB，归档需要 {required / 1024 / 1024} MB");
            }
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "磁盘空间检查跳过（无法读取目标盘容量）：{Target}", targetPath);
        }
    }

    /// <summary>保留多版本：在原文件名后加 (1)/(2)… 直到不冲突</summary>
    private static string ResolveNonConflictingPath(string target)
    {
        string dir = Path.GetDirectoryName(target)!;
        string stem = Path.GetFileNameWithoutExtension(target);
        string ext = Path.GetExtension(target);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new BusinessException($"保留多版本失败：同名文件已超过 1000 个：{target}");
    }

    /// <summary>归档同名冲突处理策略</summary>
    private enum ArchiveConflictPolicy
    {
        /// <summary>跳过（默认，不覆盖已存在文件）</summary>
        Skip,
        /// <summary>升级替换（仅当新文件更大才替换）</summary>
        Overwrite,
        /// <summary>保留多版本（加序号后缀共存）</summary>
        KeepBoth,
        /// <summary>询问：转入待确认队列，由人工裁定是否覆盖（落 AwaitingReview + NameCollision）</summary>
        Ask,
    }

    private static ParsedInfo ParseInfo(string parsedInfoJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(parsedInfoJson);
            JsonElement root = doc.RootElement;
            string? title = root.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            int? year = TryGetInt(root, "year");
            int? season = TryGetInt(root, "season");
            int? episode = TryGetInt(root, "episode");
            int? episodeEnd = TryGetInt(root, "episodeEnd");
            string? seasonTitle = root.TryGetProperty("seasonTitle", out JsonElement st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : null;
            // 源文件名命名空间季集（剧集组翻译路径才有；旧记录 / 普通路径缺这些键 → null）
            int? originalSeason = TryGetInt(root, "originalSeason");
            int? originalEpisode = TryGetInt(root, "originalEpisode");
            int? originalEpisodeEnd = TryGetInt(root, "originalEpisodeEnd");
            return new ParsedInfo(title, year, season, episode, episodeEnd, seasonTitle,
                originalSeason, originalEpisode, originalEpisodeEnd);
        }
        catch (JsonException ex)
        {
            throw new BusinessException("ParsedInfo JSON 解析失败", ex);
        }
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out int v) => v,
            JsonValueKind.String when int.TryParse(el.GetString(), out int v) => v,
            _ => null,
        };
    }

    private static string JoinFirst(IReadOnlyList<string>? list)
    {
        if (list is null || list.Count == 0) return string.Empty;
        return list[0];
    }

    private static IReadOnlyList<string>? ExtractGenreNames(string? genresJson)
    {
        if (string.IsNullOrWhiteSpace(genresJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(genresJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            List<string> names = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("name", out JsonElement nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && nameEl.GetString() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
            return names.Count == 0 ? null : names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>归档服务内部用 DTO（不同于 Domain.MediaItems.ParsedInfo）；EpisodeEnd 用于双集合并归档命名 SxxEaa-Ebb；SeasonTitle 为文件解析篇章名（TMDB 季名缺失时的季文件夹后缀回退）</summary>
    // Original{Season,Episode,EpisodeEnd}：源文件名命名空间的季集（仅剧集组翻译重排时非空）。
    // 字幕等伴生文件归属匹配用 Original*（与磁盘文件名同命名空间），归档命名仍用正典 Season/Episode。
    private sealed record ParsedInfo(string? Title, int? Year, int? Season, int? Episode, int? EpisodeEnd, string? SeasonTitle,
        int? OriginalSeason = null, int? OriginalEpisode = null, int? OriginalEpisodeEnd = null);
}
