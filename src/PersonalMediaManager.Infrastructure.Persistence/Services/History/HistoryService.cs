using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.History;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.History;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.History;

/// <summary>历史服务实现（D7.8）</summary>
/// <remarks>
/// 列表：按 UpdatedAt 倒序，支持 status/parseSource/categoryId/from/to/keyword 过滤；
/// 上限 PageSize=100，Skip 由 Page 转换得到。
/// Rescan：仅 Failed 状态允许（状态机 §6.2 唯一支持 Failed→Queued 转移）；
/// 其他非终态视为「正在处理中」，终态（Completed/Skipped/Ignored）也视为「正在处理中」对外（保持错误信息一致避免暴露状态机细节）。
/// 源文件存在性硬校验：File.Exists==false → 抛 "源文件已不存在，无法重新处理"，避免重投空文件。
/// 重投后立即 EnqueueAsync 到主管线 channel（与 ScanService 一样的入口）。
/// </remarks>
internal sealed class HistoryService : IHistoryService
{
    private const int ListMaxPageSize = 100;
    private const int KeywordMaxLength = 100;

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly Application.Common.IPendingFileQueue _queue;
    private readonly ITaskCancellationManager _taskCancellation;
    private readonly IArchiveService _archive;
    private readonly IFileMover _fileMover;
    private readonly IFileProbe _fileProbe;
    private readonly IWebhookEmitter _webhook;
    private readonly IFolderSeriesCache _folderCache;
    private readonly ITaskNotifier _notifier;
    private readonly AppPaths _paths;
    private readonly ILogger<HistoryService> _logger;

    public HistoryService(
        IDbContextFactory<PmmDbContext> dbFactory,
        Application.Common.IPendingFileQueue queue,
        ITaskCancellationManager taskCancellation,
        IArchiveService archive,
        IFileMover fileMover,
        IFileProbe fileProbe,
        IWebhookEmitter webhook,
        IFolderSeriesCache folderCache,
        ITaskNotifier notifier,
        AppPaths paths,
        ILogger<HistoryService> logger)
    {
        _dbFactory = dbFactory;
        _queue = queue;
        _taskCancellation = taskCancellation;
        _archive = archive;
        _fileMover = fileMover;
        _fileProbe = fileProbe;
        _webhook = webhook;
        _folderCache = folderCache;
        _notifier = notifier;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>手动移动时间线步骤 JSON 配置（camelCase、省 null、CJK 不转义，与 ProcessFileService 一致）</summary>
    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<HistoryListPage> ListAsync(HistoryListQuery query, CancellationToken ct = default)
    {
        // page 范围校验（page ≥ 1）已上移至 HistoryListQuery DataAnnotations，模型绑定阶段拦截
        int pageSize = query.PageSize <= 0 ? 20 : Math.Min(query.PageSize, ListMaxPageSize);
        if (query.From is not null && query.To is not null && query.From > query.To)
            throw new BusinessException("时间区间格式错误");

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<MediaItem> q = db.MediaItems.AsNoTracking();

        if (query.Status is not null) q = q.Where(m => m.Status == query.Status.Value);
        if (query.ParseSource is not null) q = q.Where(m => m.ParseSource == query.ParseSource.Value);
        if (query.CategoryId is not null) q = q.Where(m => m.CategoryId == query.CategoryId.Value);
        if (query.From is not null) q = q.Where(m => m.UpdatedAt >= query.From.Value);
        if (query.To is not null) q = q.Where(m => m.UpdatedAt <= query.To.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string kw = query.Keyword.Trim();
            if (kw.Length > KeywordMaxLength) kw = kw[..KeywordMaxLength];
            string pattern = $"%{kw}%";
            // SQLite 的 LIKE 默认大小写不敏感（ASCII）；标题用 ParsedInfo JSON 字符串内嵌即可
            q = q.Where(m => EF.Functions.Like(m.FileName, pattern)
                || (m.ParsedInfo != null && EF.Functions.Like(m.ParsedInfo, pattern)));
        }

        int total = await q.CountAsync(ct);

        var rows = await (
            from m in q
            orderby m.UpdatedAt descending
            join c in db.CategoryDefinitions.AsNoTracking() on m.CategoryId equals c.Id into gj
            from c in gj.DefaultIfEmpty()
            select new
            {
                m.Id, m.FileName, m.FileSize, m.Status, m.ParseSource, m.Confidence,
                m.TargetPath, m.ArchivedAt, m.UpdatedAt, m.ParsedInfo,
                m.TmdbId, m.TmdbMediaType, m.HasIncompatibleAudio,
                CategoryName = c == null ? null : c.Name,
            }
        ).Skip((query.Page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // 「待补元数据」徽标：对当页 item ids 批量一次查询 Archiving 时间线后内存组装（杜绝逐行 N+1）
        Dictionary<long, bool> metadataPendingMap =
            await LoadMetadataPendingMapAsync(db, rows.Select(r => r.Id).ToList(), ct);

        List<HistoryItemResponse> items = new(rows.Count);
        foreach (var r in rows)
        {
            // r1 P1-8：title/year/season/episode/episodeEnd 从 ParsedInfo JSON 解析后平铺到响应字段，
            // 让前端表格直接读 row.title / row.season / row.fileSize（避免每行再 JSON.parse(row.parsedInfo)）
            (string? title, int? year, int? season, int? episode, int? episodeEnd) = ExtractParsedInfo(r.ParsedInfo);
            items.Add(new HistoryItemResponse(
                r.Id, r.FileName, r.FileSize, title, year, season, episode, episodeEnd,
                r.Status, r.ParseSource, r.Confidence,
                r.CategoryName, r.TargetPath, r.ArchivedAt, r.UpdatedAt,
                r.TmdbId, r.TmdbMediaType,
                metadataPendingMap.TryGetValue(r.Id, out bool metadataPending) && metadataPending,
                r.HasIncompatibleAudio));
        }

        return new HistoryListPage(items, total, query.Page, pageSize);
    }

    /// <summary>批量判定当页记录的「待补元数据」徽标（每条记录取最后一条 Archiving 步骤的 detail 判定）</summary>
    /// <remarks>
    /// 徽标口径 =「该 item **最后一条** stage=Archiving 步骤的 detail 含 "metadataPending":true」。选择依据（实测三个写入点）：
    /// 一次归档（自动管线 ProcessFileService / 审核确认 ReviewService / 手动移动 ManualArchiveAsync）只写**一条** Archiving 步骤，
    /// operation 与 metadataPending 在**同一条** detail JSON 中（不存在拆成两条的先后问题）；
    /// 撤销归档（undo）/ 退回重改（reopen 的 MOVE_BACK / DELETE_COPY）/ 冲突跳过（conflict）同样写 Archiving 步骤但**不带**该字段。
    /// 因此「最后一条 Archiving 步骤」即「最近一次归档动作的完整快照」：带警告归档 → 点亮；
    /// 退回重改（追加移回步骤）→ 熄灭；重新确认成功（追加无警告归档步骤）→ 保持熄灭；再次降级归档 → 重新点亮。
    /// 与 WasLastArchiveCopyAsync（读最近一条 Archiving 步骤的 operation 判 Move/Copy）同一「最新归档快照」判定哲学，排序口径一致。
    /// detail 由 StepJsonOptions 序列化（camelCase、无缩进、省 null）：待补时必含子串 "metadataPending":true，
    /// 无待补时整字段省略（绝不落 false），故 SQL 侧 LIKE 子串匹配即可布尔投影，不必把大 JSON 拉回内存。
    /// </remarks>
    private static async Task<Dictionary<long, bool>> LoadMetadataPendingMapAsync(
        PmmDbContext db, IReadOnlyList<long> mediaItemIds, CancellationToken ct)
    {
        if (mediaItemIds.Count == 0) return new Dictionary<long, bool>();

        var archivingSteps = await db.ProcessSteps.AsNoTracking()
            .Where(s => mediaItemIds.Contains(s.MediaItemId) && s.Stage == MediaItemStatus.Archiving)
            .Select(s => new
            {
                s.MediaItemId,
                s.Id,
                s.StartedAt,
                MetadataPending = s.Detail != null && EF.Functions.Like(s.Detail, "%\"metadataPending\":true%"),
            })
            .ToListAsync(ct);

        // 内存组装：每个 item 取最后一条 Archiving 步骤（StartedAt 降序 + Id 降序兜底，同 WasLastArchiveCopyAsync）
        return archivingSteps
            .GroupBy(s => s.MediaItemId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id).First().MetadataPending);
    }

    public async Task<HistoryListPage> ListPendingAsync(PendingListQuery query, CancellationToken ct = default)
    {
        // page 范围校验（page ≥ 1）已上移至 PendingListQuery DataAnnotations，模型绑定阶段拦截
        int pageSize = query.PageSize <= 0 ? 20 : Math.Min(query.PageSize, ListMaxPageSize);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<MediaItem> q = db.MediaItems.AsNoTracking()
            .Where(m => MediaItemStatusGroups.InFlight.Contains(m.Status));

        int total = await q.CountAsync(ct);

        // 先进先出：Id 单调递增即创建顺序，串行管线下最旧的在途任务即「当前/下一个」处理项，置于队首
        var rows = await (
            from m in q
            orderby m.Id ascending
            join c in db.CategoryDefinitions.AsNoTracking() on m.CategoryId equals c.Id into gj
            from c in gj.DefaultIfEmpty()
            select new
            {
                m.Id, m.FileName, m.FileSize, m.Status, m.ParseSource, m.Confidence,
                m.TargetPath, m.ArchivedAt, m.UpdatedAt, m.ParsedInfo,
                m.TmdbId, m.TmdbMediaType, m.HasIncompatibleAudio,
                CategoryName = c == null ? null : c.Name,
            }
        ).Skip((query.Page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        List<HistoryItemResponse> items = new(rows.Count);
        foreach (var r in rows)
        {
            (string? title, int? year, int? season, int? episode, int? episodeEnd) = ExtractParsedInfo(r.ParsedInfo);
            items.Add(new HistoryItemResponse(
                r.Id, r.FileName, r.FileSize, title, year, season, episode, episodeEnd,
                r.Status, r.ParseSource, r.Confidence,
                r.CategoryName, r.TargetPath, r.ArchivedAt, r.UpdatedAt,
                r.TmdbId, r.TmdbMediaType));
        }

        return new HistoryListPage(items, total, query.Page, pageSize);
    }

    public async Task<MediaItemDetailResponse> GetDetailAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        string? categoryName = null;
        if (item.CategoryId is long catId)
        {
            categoryName = await db.CategoryDefinitions.AsNoTracking()
                .Where(c => c.Id == catId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
        }

        TmdbMetadataSummary? tmdb = null;
        if (item.TmdbId is int tmdbId && !string.IsNullOrEmpty(item.TmdbMediaType))
        {
            string mediaType = item.TmdbMediaType!;
            TmdbMetadataCache? cache = await db.TmdbMetadataCaches.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TmdbId == tmdbId && c.MediaType == mediaType, ct);
            if (cache is not null)
            {
                tmdb = new TmdbMetadataSummary(
                    cache.TmdbId, cache.MediaType, cache.Title, cache.OriginalTitle,
                    cache.Year, cache.TotalSeasons, cache.PosterPath,
                    cache.OriginCountry, cache.OriginalLanguage, cache.Overview, cache.CachedAt);
            }
        }

        List<AiCallEntryResponse> aiCalls = await db.AuditAiCalls.AsNoTracking()
            .Where(a => a.MediaItemId == mediaItemId)
            .OrderBy(a => a.Timestamp)
            .Select(a => new AiCallEntryResponse(
                a.Id, a.ProviderId, a.Success, a.LatencyMs,
                a.ErrorType, a.ErrorDetail, a.Timestamp))
            .ToListAsync(ct);

        // 处理时间线步骤（由 ProcessFileService 在每个 Transition 时追加；按 StartedAt 顺序）
        List<ProcessStepEntry> steps = await db.ProcessSteps.AsNoTracking()
            .Where(s => s.MediaItemId == mediaItemId)
            .OrderBy(s => s.StartedAt)
            .ThenBy(s => s.Id)
            .Select(s => new ProcessStepEntry(s.Id, s.Stage, s.StartedAt, s.DurMs, s.Detail))
            .ToListAsync(ct);

        return new MediaItemDetailResponse(
            item.Id, item.SourcePath, item.FileName, item.FileSize, item.FileHash,
            item.Status, item.ParseSource, item.Confidence, item.ParsedInfo,
            item.TmdbId, item.TmdbMediaType, item.CategoryId, categoryName,
            item.TargetPath, item.ErrorMessage, item.AttemptCount, item.LastAttemptAt,
            item.ArchivedAt, item.CreatedAt, item.UpdatedAt,
            tmdb, aiCalls, steps);
    }

    public async Task<CancelTaskResult> CancelAsync(long mediaItemId, CancellationToken ct = default)
    {
        string sourcePath;
        await using (PmmDbContext initialDb = await _dbFactory.CreateDbContextAsync(ct))
        {
            var current = await initialDb.MediaItems.AsNoTracking()
                .Where(m => m.Id == mediaItemId)
                .Select(m => new { m.SourcePath, m.Status })
                .FirstOrDefaultAsync(ct);
            if (current is null) throw new BusinessException("记录不存在");
            if (!MediaItemStatusGroups.Cancellable.Contains(current.Status))
                throw new BusinessException("仅排队或归档前正在处理的任务可以取消");
            sourcePath = current.SourcePath;
        }

        Task? activeCompletion = _taskCancellation.RequestCancellation(sourcePath);
        bool retainRequestUntilDequeue = false;
        try
        {
            if (activeCompletion is not null)
                await activeCompletion;

            // 一旦已发出用户取消，必须完成一致性收尾；不能因 HTTP 客户端断开而把记录遗留在中间态。
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(
                m => m.Id == mediaItemId, CancellationToken.None);
            if (item is null) throw new BusinessException("记录不存在");
            if (item.Status == MediaItemStatus.Cancelled)
                return new CancelTaskResult(item.Id, item.Status);
            if (!MediaItemStatusGroups.Cancellable.Contains(item.Status))
                throw new BusinessException("任务已经结束，无法取消");

            MediaItemStatus oldStatus = item.Status;
            item.Transition(MediaItemStatus.Cancelled);
            item.AppendStep(MediaItemStatus.Cancelled, DateTimeOffset.UtcNow, durMs: 0,
                JsonSerializer.Serialize(new { reason = "用户主动取消", fromStage = oldStatus.ToString() }, StepJsonOptions));
            await db.SaveChangesAsync(CancellationToken.None);
            await _notifier.NotifyStatusChangedAsync(new TaskStatusChangedEvent(
                item.Id, item.FileName, oldStatus, item.Status, DateTimeOffset.UtcNow), CancellationToken.None);

            // Channel 不能删指定排队项：保留取消标记，待 Worker 真正读到后 Register 会立即得到已取消 token 并清理。
            retainRequestUntilDequeue = activeCompletion is null;

            return new CancelTaskResult(item.Id, item.Status);
        }
        finally
        {
            if (!retainRequestUntilDequeue)
                _taskCancellation.ClearRequest(sourcePath);
        }
    }

    public async Task<RescanResult> RescanAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        // 状态机硬约束：只允许 Failed→Queued
        if (item.Status != MediaItemStatus.Failed)
        {
            throw new BusinessException("任务正在处理中，请稍后重试");
        }

        if (!File.Exists(item.SourcePath))
        {
            throw new BusinessException("源文件已不存在，无法重新处理");
        }

        item.Transition(MediaItemStatus.Queued);
        // 清理上次失败的痕迹（保留 AttemptCount 累计）
        item.ClearError();
        await db.SaveChangesAsync(ct);

        // 反查 WatchFolderId：保证 Rescan 重投后下游 ProcessFileService 仍能算出 watchRoot，
        // 进而 FileParseContext.RelativeSegments 能携带「监控根→文件」的所有中间层目录给规则引擎与 AI 兜底。
        // 不带 watchRoot 的话，FileParseContext.FromFullPath 会退化到「只取直接父目录单段」，
        // 多层路径上下文丢失（例：F:\迅雷下载\国务卿女士 6季\第1季\01.mp4 会丢「国务卿女士 6季」这一层）。
        long resolvedWatchFolderId = await TryResolveWatchFolderIdAsync(item.SourcePath, db, ct);

        try
        {
            await _queue.EnqueueAsync(
                new Application.Common.PendingFileItem(item.SourcePath, resolvedWatchFolderId,
                    Application.Common.PendingFileSource.Manual), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重投入队失败：MediaItemId={MediaItemId}", mediaItemId);
            throw new BusinessException("重投任务失败", ex);
        }

        return new RescanResult(item.Id, item.Status);
    }

    // ---------- 测试路径（只读体检）/ 手动移动 ----------

    public async Task<ArchivePreviewResult> PreviewArchiveAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        List<ArchivePreviewCheck> checks = new();

        // 1. 源文件存在
        bool sourceExists = _fileProbe.FileExists(item.SourcePath);
        checks.Add(new ArchivePreviewCheck("源文件存在", sourceExists,
            sourceExists ? item.SourcePath : "源文件已不存在，无法移动 / 复制"));

        // 2. 分类目标根
        CategoryDefinition? cat = null;
        if (item.CategoryId is long catId)
        {
            cat = await db.CategoryDefinitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == catId, ct);
        }
        bool hasCategory = cat is not null;
        checks.Add(new ArchivePreviewCheck("已配置分类目标根", hasCategory,
            hasCategory ? cat!.TargetRoot : "记录尚未分类（无 TargetRoot），无法计算归档去向"));

        // 3. TMDB 匹配
        bool hasTmdb = item.TmdbId is int && !string.IsNullOrEmpty(item.TmdbMediaType);
        checks.Add(new ArchivePreviewCheck("已匹配 TMDB", hasTmdb,
            hasTmdb ? $"tmdbId={item.TmdbId} ({item.TmdbMediaType})" : "记录尚未匹配 TMDB"));

        // TMDB 规范标题缓存：标题段与实际归档（ArchiveService）同一回退链，预览展示即真实落点
        TmdbMetadataCache? meta = null;
        if (hasTmdb)
        {
            string mediaType = item.TmdbMediaType!;
            meta = await db.TmdbMetadataCaches.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TmdbId == item.TmdbId!.Value && c.MediaType == mediaType, ct);
        }

        // 4. 解析信息
        ParsedInfo? info = ParsedInfo.FromJson(item.ParsedInfo);
        checks.Add(new ArchivePreviewCheck("解析信息可读", info is not null,
            info is not null ? null : "ParsedInfo 缺失或损坏"));

        bool isTv = string.Equals(item.TmdbMediaType, "tv", StringComparison.OrdinalIgnoreCase);
        // 标题语言回退链与实际归档一致：TMDB 中文名 → TMDB 原文名 → 文件解析名（杂质名兜底）
        string? title = ArchiveFolderResolver.FirstNonBlank(meta?.Title, meta?.OriginalTitle, info?.Title);
        int? year = info?.Year;
        int? season = info?.Season;
        int? episode = info?.Episode;
        int? episodeEnd = info?.EpisodeEnd;

        // 5. 字段齐全性
        bool titleOk = !string.IsNullOrWhiteSpace(title);
        checks.Add(new ArchivePreviewCheck("标题非空", titleOk, titleOk ? title : "标题缺失"));
        bool yearOk = year is not null;
        checks.Add(new ArchivePreviewCheck("年份存在", yearOk,
            yearOk ? year!.Value.ToString() : "年份缺失，无法按 Plex 命名"));
        bool tvFieldsOk = !isTv || (season is not null && episode is not null);
        if (isTv)
        {
            checks.Add(new ArchivePreviewCheck("剧集季 / 集齐全", tvFieldsOk,
                tvFieldsOk
                    ? $"S{season:D2}E{episode:D2}{(episodeEnd is int ee && ee > episode ? $"-E{ee:D2}" : string.Empty)}"
                    : $"剧集缺季 / 集（season={season?.ToString() ?? "?"} / episode={episode?.ToString() ?? "?"}）"));
        }

        // 6. Plex 命名 + 路径穿越 + 同名冲突
        string? relativePath = null;
        string? targetPath = null;
        bool targetExists = false;
        string? error = null;

        string extension = Path.GetExtension(item.SourcePath);
        bool extOk = !string.IsNullOrEmpty(extension);
        if (!extOk)
        {
            error = "源文件无扩展名，无法归档";
            checks.Add(new ArchivePreviewCheck("源文件有扩展名", false, error));
        }
        else if (hasCategory && hasTmdb && titleOk && yearOk && tvFieldsOk)
        {
            try
            {
                // 与实际归档（ArchiveService）/ 审核去向预览（ReviewService）共用 ArchiveFolderResolver：
                // 标题规范化 + 分类根下 {tmdb-NNN} 已存在目录复用 + 多季「该季首播年 / 季标题」差异化命名，
                // 从根上杜绝「预览 ≠ 实际落点」；同名冲突体检（下方）也因此探测的是真实落点。
                int? seasonYear = null;
                string? seasonTitle = null;
                if (isTv)
                    (seasonYear, seasonTitle) = ArchiveFolderResolver.ResolveSeasonNaming(meta?.RawJson, info?.SeasonTitle, season!.Value);
                // 体检预览读同一套归档命名模板（与实际归档逐字节一致；非法 / 缺失回退默认）
                NamingTemplateOptions namingTemplate = await ArchiveNamingTemplateReader.ReadAsync(db, _logger, ct);
                relativePath = ArchiveFolderResolver.BuildRelativeTargetPath(
                    title!, year!.Value, item.TmdbId!.Value, isTv,
                    season ?? 0, episode ?? 0, episodeEnd, extension, cat!.TargetRoot, _logger,
                    seasonYear, seasonTitle, namingTemplate, meta?.OriginalTitle);
                checks.Add(new ArchivePreviewCheck("Plex 命名校验", true, relativePath));

                string candidate = Path.Combine(cat!.TargetRoot, relativePath);
                if (PathSafetyGuard.IsWithinRoot(cat.TargetRoot, candidate))
                {
                    targetPath = candidate;
                    checks.Add(new ArchivePreviewCheck("目标在分类根之内（无路径穿越）", true, candidate));
                    targetExists = _fileProbe.FileExists(targetPath);
                    checks.Add(new ArchivePreviewCheck("目标无同名冲突", !targetExists,
                        targetExists ? "目标已存在同名文件（按归档冲突策略可能跳过 / 保留多版本 / 升级替换）" : "目标位置空闲"));
                }
                else
                {
                    error = "目标路径越出分类根目录（路径穿越拦截）";
                    checks.Add(new ArchivePreviewCheck("目标在分类根之内（无路径穿越）", false, error));
                }
            }
            catch (ArgumentException ex)
            {
                // 越界集号 / episodeEnd < episode / 标题清洗后为空 等命名校验失败 —— 「最后一部路径错误」的精确成因
                error = $"Plex 命名校验未通过：{ex.Message}";
                checks.Add(new ArchivePreviewCheck("Plex 命名校验", false, error));
            }
        }
        else
        {
            error = "前置信息不全，无法计算归档去向（见上方未通过项）";
        }

        // 可移动 = 路径算得出 + 无阻断错误 + 源文件在（同名冲突属软提示，不阻断）
        bool canArchive = error is null && relativePath is not null && sourceExists;

        return new ArchivePreviewResult(
            item.Id, item.Status, item.SourcePath, sourceExists,
            item.CategoryId, cat?.Name, cat?.TargetRoot,
            isTv, title, year, season, episode, episodeEnd,
            relativePath, targetPath, targetExists, canArchive, error, checks);
    }

    public async Task<ManualArchiveResult> ManualArchiveAsync(long mediaItemId, ManualArchiveRequest req, CancellationToken ct = default)
    {
        ArchiveOperation operation = ParseOperation(req?.Mode);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        // 仅 Failed / Skipped / AwaitingReview 可手动移动（终态完成 / 已忽略 / 在途处理中拒绝）
        if (item.Status is not (MediaItemStatus.Failed or MediaItemStatus.Skipped or MediaItemStatus.AwaitingReview))
        {
            throw new BusinessException($"当前状态（{item.Status}）不支持手动移动；仅失败 / 跳过 / 待确认记录可手动移动");
        }

        // 前置元数据齐全性（缺则提示先重新处理 / 在审核页确认）
        if (item.CategoryId is null)
            throw new BusinessException("记录尚未分类，无法手动移动（请先重新处理或在审核页确认分类）");
        if (item.TmdbId is null || string.IsNullOrEmpty(item.TmdbMediaType))
            throw new BusinessException("记录尚未匹配 TMDB，无法手动移动（请先重新处理或在审核页确认）");
        if (string.IsNullOrEmpty(item.ParsedInfo))
            throw new BusinessException("记录缺少解析信息，无法手动移动");
        if (!_fileProbe.FileExists(item.SourcePath))
            throw new BusinessException("源文件已不存在，无法手动移动");

        // 人工干预逃生口：直接置 Archiving（绕过前向状态机）
        item.BeginManualArchive();
        // 落 Archiving 的同时清空 stale TargetPath：Skipped（冲突跳过）时 TargetPath 可能记着
        // 「目标位他人文件」的路径——带着旧值进入 Archiving，一旦归档中途崩溃，启动恢复
        // （StartupRecoveryWorker 见 TargetPath 文件存在即判 Completed）会把本条误判为假完成。
        // 聚合未提供单独清空入口（成功侧由 SetArchiveResult 重写），此处经 EF 属性入口清空（Persistence 层合法操作）。
        db.Entry(item).Property(nameof(MediaItem.TargetPath)).CurrentValue = null;
        await db.SaveChangesAsync(ct);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            ArchiveResult arc = await _archive.ArchiveAsync(item, operation, ct);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long durMs = (long)Math.Max(0d, (now - startedAt).TotalMilliseconds);

            item.SetArchiveResult(arc.TargetPath);
            // 视频已落地但 nfo/Webhook 失败时（arc.Warnings 非空），时间线标记「待补元数据」，仍判 Completed 不回退
            bool metadataPending = arc.Warnings is { Count: > 0 };
            // 时间线补一条归档步骤（前端按 stage=Archiving 渲染 operation / source / target）
            item.AppendStep(MediaItemStatus.Archiving, startedAt, durMs, SerializeStep(new
            {
                operation = operation == ArchiveOperation.Copy ? "COPY" : "MOVE",
                source = item.SourcePath,
                target = arc.TargetPath,
                manual = true,
                metadataPending = metadataPending ? true : (bool?)null, // 省 null：无待补时不落该字段
                warnings = metadataPending ? arc.Warnings : null,
            }));

            MediaItemStatus next = arc.Outcome == ArchiveOutcome.Completed
                ? MediaItemStatus.Completed
                : MediaItemStatus.Skipped;
            item.Transition(next);
            item.AppendStep(next, now, durMs: 0, next == MediaItemStatus.Completed
                ? SerializeStep(new { target = arc.TargetPath, manual = true })
                : SerializeStep(new { reason = "目标已存在同名文件（手动移动冲突跳过）", manual = true }));
            await db.SaveChangesAsync(ct);

            if (metadataPending)
                _logger.LogWarning("手动移动完成但元数据待补（{Op}）：{Source} → {Target}（待补：{Warnings}）",
                    operation, item.SourcePath, arc.TargetPath, string.Join("；", arc.Warnings!));
            else
                _logger.LogInformation("手动移动完成（{Op}）：{Source} → {Target}（{Outcome}）",
                    operation, item.SourcePath, arc.TargetPath, arc.Outcome);

            // 冲突跳过同样是终局事件：补发 media.skipped（自动管线 ProcessFileService 同口径；media.archived 由 ArchiveService 落地时自发）
            if (arc.Outcome != ArchiveOutcome.Completed)
                await EmitSkippedAsync(item, "目标已存在同名文件（手动移动冲突跳过）", ct);

            // nfo / Webhook 降级警告透出到 API 结果：前端 toast 可提示「已落地但元数据待补」，不再静默成功
            return new ManualArchiveResult(item.Id, item.Status, arc.TargetPath,
                arc.Outcome == ArchiveOutcome.Completed ? "Completed" : "Skipped",
                metadataPending ? arc.Warnings : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户中断（请求取消 / 应用关停）：先把记录收尾到可恢复终态 Failed 再透传取消——
            // 否则记录永卡 Archiving（无任何机制再推进）。收尾与旁路通知一律用 CancellationToken.None
            // （MarkManualArchiveFailedAsync 内部固定 None）：补偿是恢复一致性的关键动作，不应被取消打断。
            await MarkManualArchiveFailedAsync(db, item, "归档被用户中断");
            await EmitFailedAsync(item, "归档被用户中断", CancellationToken.None);
            throw;
        }
        catch (BusinessException ex)
        {
            // ArchiveService 抛的业务异常（缺字段 / 磁盘不足 / 路径穿越）→ 透传，但先把记录拉回 Failed
            await MarkManualArchiveFailedAsync(db, item, "手动移动失败：业务校验未通过");
            await EmitFailedAsync(item, ex.Message, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手动移动失败：MediaItemId={MediaItemId}", mediaItemId);
            await MarkManualArchiveFailedAsync(db, item, ex.Message);
            await EmitFailedAsync(item, $"手动移动失败：{ex.Message}", CancellationToken.None);
            throw new BusinessException($"手动移动失败：{ex.Message}", ex);
        }
    }

    /// <summary>解析移动模式字符串（缺省 / Move → Move；Copy → Copy；其它 → 1000）</summary>
    private static ArchiveOperation ParseOperation(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "Move", StringComparison.OrdinalIgnoreCase))
            return ArchiveOperation.Move;
        if (string.Equals(mode, "Copy", StringComparison.OrdinalIgnoreCase))
            return ArchiveOperation.Copy;
        throw new BusinessException($"未知的移动模式：{mode}（仅支持 Move / Copy）");
    }

    /// <summary>手动移动失败兜底：非终态时 MarkFailed + 落库（标记失败再失败仅吞咽，不掩盖原异常）</summary>
    /// <remarks>
    /// 落库固定用 CancellationToken.None：失败补偿是恢复一致性的关键动作，不应被已取消的请求令牌打断
    /// ——否则「失败 / 中断 + 取消」同时发生时补偿落库立即被取消、记录永卡 Archiving（与 TryRollbackUndoFileAsync 同口径）。
    /// </remarks>
    private static async Task MarkManualArchiveFailedAsync(PmmDbContext db, MediaItem item, string reason)
    {
        try
        {
            if (!item.IsTerminal())
            {
                item.MarkFailed(reason);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch
        {
            // 兜底：补偿失败不应掩盖原始异常（原异常会由 catch 链继续抛给上层）
        }
    }

    /// <summary>发 media.failed Webhook（手动移动失败终态）；payload 口径与 ProcessFileService 一致，实现侧自吞异常不阻断主流程</summary>
    private Task EmitFailedAsync(MediaItem media, string error, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaFailed, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            error,
        }, ct);

    /// <summary>发 media.skipped Webhook（手动移动同名冲突跳过）；payload 口径与 ProcessFileService 一致，实现侧自吞异常不阻断主流程</summary>
    private Task EmitSkippedAsync(MediaItem media, string reason, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaSkipped, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            targetPath = media.TargetPath,
            reason,
        }, ct);

    private static string SerializeStep(object detail) => JsonSerializer.Serialize(detail, StepJsonOptions);

    public async Task<UndoArchiveResult> UndoArchiveAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        // 仅「已完成」归档可撤销（其它状态归档尚未完成或本就未归档，无撤销语义）
        if (item.Status != MediaItemStatus.Completed)
            throw new BusinessException("仅「已完成」归档记录可撤销");
        if (string.IsNullOrWhiteSpace(item.TargetPath))
            throw new BusinessException("记录缺少归档目标路径，无法撤销");

        string targetPath = item.TargetPath!;
        string sourcePath = item.SourcePath;

        // 归档文件必须仍在目标位（被外部移走 / 删除 / 改名则无从撤销，以 MediaItem.TargetPath 列为准——KeepBoth 改名后此列已是实际落点）
        if (!_fileProbe.FileExists(targetPath))
            throw new BusinessException("归档文件已不存在（可能被外部移动 / 删除 / 改名），无法撤销");

        // 身份校验：撤销搬动 / 删除的必须是「本条记录当初归档落下的那份文件」，防误搬他人产物
        EnsureArchivedFileIdentity(item, targetPath);

        // 反向移回归档文件到源位（撤销 / 退回重改共用 MoveArchivedBackAsync）；按「撤销」场景包装文件冲突 / 缺失文案
        DateTimeOffset startedAt;
        string operation;
        try
        {
            (operation, startedAt) = await MoveArchivedBackAsync(db, item, ct);
        }
        catch (FileConflictException)
        {
            throw new BusinessException("源位置已存在同名文件（可能有新文件占用），撤销中止以免覆盖");
        }
        catch (FileNotFoundException)
        {
            throw new BusinessException("归档文件已不存在，无法撤销");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销归档失败：MediaItemId={MediaItemId}", mediaItemId);
            throw new BusinessException($"撤销归档失败：{ex.Message}", ex);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long durMs = (long)Math.Max(0d, (now - startedAt).TotalMilliseconds);
        // 审计：补一条「撤销归档」时间线（沿用 Archiving stage，detail 标 undo 与本次动作的源→去向）
        item.AppendStep(MediaItemStatus.Archiving, startedAt, durMs, SerializeStep(new
        {
            operation,
            source = targetPath,   // 本次动作来源 = 归档位置
            target = sourcePath,   // 去向 = 源位置（DELETE_COPY 时为保留的源）
            undo = true,
        }));
        item.UndoArchive();   // Completed → Skipped，清 TargetPath / ArchivedAt / FileMissing
        item.AppendStep(MediaItemStatus.Skipped, now, durMs: 0, SerializeStep(new
        {
            reason = operation == "DELETE_COPY" ? "撤销归档：已删除归档副本（源文件保留）" : "撤销归档：归档文件已移回源位置",
            undo = true,
        }));
        // 归档解除：清理该媒体悬空的字幕下载记录（与状态变更同事务，落库失败随补偿一并回滚）
        await RemoveSubtitleDownloadsOnUnwindAsync(db, item.Id, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception saveEx)
        {
            // 文件已移回 / 副本已删，但状态落库失败（并发改 RowVersion / DB 锁 / IO）：
            // 补偿——把文件移回归档位，恢复撤销前的一致状态，避免「DB 仍 Completed@target 但文件已在 source」。
            await TryRollbackUndoFileAsync(operation, sourcePath, targetPath);
            if (saveEx is DbUpdateConcurrencyException)
                throw new BusinessException("撤销时该记录已被其他操作修改，已回滚文件位置，请刷新后重试", saveEx);
            throw;
        }

        _logger.LogInformation("撤销归档完成（{Op}）：{Target} → {Source}（MediaItemId={Id}）",
            operation, targetPath, sourcePath, item.Id);
        return new UndoArchiveResult(item.Id, item.Status, sourcePath, operation);
    }

    public async Task<ReopenResult> ReopenForReviewAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        // 仅人工确认 confirm 的两个落点可退回：已完成（文件在归档位）/ 已跳过（同名冲突 / 撤销后，文件在源位）
        if (item.Status is not (MediaItemStatus.Completed or MediaItemStatus.Skipped))
            throw new BusinessException("仅「已完成」或「已跳过」记录可退回重改");

        string sourcePath = item.SourcePath;
        string? targetPath = item.TargetPath;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string operation;

        if (item.Status == MediaItemStatus.Completed)
        {
            // 已完成：本记录文件在 TargetPath，反向移回源位（同撤销归档）
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new BusinessException("记录缺少归档目标路径，无法退回");
            if (!_fileProbe.FileExists(targetPath!))
                throw new BusinessException("归档文件已不存在（可能被外部移动 / 删除 / 改名），无法退回");
            // 身份校验：退回搬动的必须是「本条记录当初归档落下的那份文件」，防误搬他人产物
            EnsureArchivedFileIdentity(item, targetPath!);
            try
            {
                (operation, startedAt) = await MoveArchivedBackAsync(db, item, ct);
            }
            catch (FileConflictException)
            {
                throw new BusinessException("源位置已存在同名文件（可能有新文件占用），退回中止以免覆盖");
            }
            catch (FileNotFoundException)
            {
                throw new BusinessException("归档文件已不存在，无法退回");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BusinessException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "退回重改失败：MediaItemId={MediaItemId}", mediaItemId);
                throw new BusinessException($"退回重改失败：{ex.Message}", ex);
            }
        }
        else
        {
            // 已跳过（同名冲突 / 忽略 / 撤销归档后）：本记录文件始终在源位，不动文件，仅校验源仍在
            if (!_fileProbe.FileExists(sourcePath))
                throw new BusinessException("源文件已不存在，无法退回重改");
            operation = "REOPEN_INPLACE";
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long durMs = (long)Math.Max(0d, (now - startedAt).TotalMilliseconds);
        if (operation != "REOPEN_INPLACE")
        {
            // 审计：补一条「退回·归档文件移回」时间线（沿用 Archiving stage，detail 标 reopen）
            item.AppendStep(MediaItemStatus.Archiving, startedAt, durMs, SerializeStep(new
            {
                operation,
                source = targetPath,   // 来源 = 归档位置
                target = sourcePath,   // 去向 = 源位置
                reopen = true,
            }));
        }
        item.ReopenForReview();   // Completed/Skipped → AwaitingReview（ManualReopen），清 TargetPath / ArchivedAt
        item.AppendStep(MediaItemStatus.AwaitingReview, now, durMs: 0, SerializeStep(new
        {
            reason = operation == "REOPEN_INPLACE"
                ? "退回重改：记录退回待人工确认（文件保留源位）"
                : "退回重改：归档文件已移回源位置，记录退回待人工确认",
            reopen = true,
        }));
        // 归档解除：清理该媒体悬空的字幕下载记录（REOPEN_INPLACE 从未归档完成 → 无记录，为空操作）
        await RemoveSubtitleDownloadsOnUnwindAsync(db, item.Id, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception saveEx)
        {
            // 文件已移回（MOVE_BACK）但状态落库失败：补偿移回归档位恢复一致（REOPEN_INPLACE 没动文件 / DELETE_COPY 副本已删无法补偿）
            if (operation == "MOVE_BACK")
                await TryRollbackUndoFileAsync(operation, sourcePath, targetPath!);
            if (saveEx is DbUpdateConcurrencyException)
                throw new BusinessException("退回时该记录已被其他操作修改，已回滚文件位置，请刷新后重试", saveEx);
            throw;
        }

        // 退回重改 = 用户否定了本次匹配结果：失效该目录的文件夹剧集复用缓存，
        // 防「退回后、重新确认前」窗口内同目录新文件仍复用被否定的旧 tmdbId（与 ReviewService 失效口径一致）
        InvalidateFolderCache(item.SourcePath);

        _logger.LogInformation("退回重改完成（{Op}）：MediaItemId={Id} → AwaitingReview", operation, item.Id);
        return new ReopenResult(item.Id, item.Status, sourcePath, operation);
    }

    /// <summary>失效源文件所属目录的文件夹剧集复用缓存（键口径与 ProcessFileService.TryFolderKey 一致）</summary>
    private void InvalidateFolderCache(string sourcePath)
    {
        string? folderKey;
        try { folderKey = Path.GetDirectoryName(sourcePath); }
        catch { return; }
        if (!string.IsNullOrEmpty(folderKey)) _folderCache.Remove(folderKey);
    }

    /// <summary>反向移回归档文件到源位（撤销归档 / 退回重改共用的纯文件动作）</summary>
    /// <remarks>
    /// 按最近一条 Archiving 时间线的 operation 判断当初 Move/Copy：
    ///   - 当初 Copy 且源仍在 → 删归档副本（DELETE_COPY，不动源；共用 tvshow.nfo / poster.jpg 不删）；
    ///   - 否则（Move，或 Copy 但源已不在，避免删唯一副本）→ 反向 move 归档文件回源位（MOVE_BACK，连同 stem 字幕、删本次 .nfo）。
    /// 不含 try/catch、不改状态、不落库：FileConflict / FileNotFound 等异常由调用方按场景（撤销 / 退回）包装文案。
    /// 仅已完成归档（文件确在 TargetPath）可调；调用方须先校验 TargetPath 非空 + 归档文件存在。
    /// </remarks>
    private async Task<(string Operation, DateTimeOffset StartedAt)> MoveArchivedBackAsync(PmmDbContext db, MediaItem item, CancellationToken ct)
    {
        string targetPath = item.TargetPath!;
        string sourcePath = item.SourcePath;
        bool wasCopy = await WasLastArchiveCopyAsync(db, item.Id, ct);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string operation;
        if (wasCopy && _fileProbe.FileExists(sourcePath))
        {
            operation = "DELETE_COPY";
            DeleteArchivedArtifacts(targetPath);
        }
        else
        {
            operation = "MOVE_BACK";
            await _fileMover.MoveAsync(targetPath, sourcePath, ct);
            RelocateSidecarsBack(targetPath, sourcePath);
        }
        return (operation, startedAt);
    }

    /// <summary>校验目标位文件与归档记录身份一致（按文件大小对比），不符抛 BusinessException</summary>
    /// <remarks>
    /// 防「升级替换（Overwrite）后撤销 / 退回误搬他人文件」：替换发生后 TargetPath 上已是更高清的新文件
    /// （他人产物），旧记录再撤销 / 退回会把新文件搬走改名（或删掉新副本）。以 FileSize 对比做轻量身份校验。
    /// 宽容口径：FileSize ≤ 0（极早期旧数据未记录大小）放行；目标大小读取失败（网络盘抖动 / 权限瞬断）
    /// 同样放行——身份校验是尽力防护，后续实搬 IO 仍会把关真实错误。
    /// </remarks>
    private static void EnsureArchivedFileIdentity(MediaItem item, string targetPath)
    {
        if (item.FileSize <= 0) return; // 旧数据未记录文件大小，宽容放行
        long actualLength;
        try
        {
            actualLength = new FileInfo(targetPath).Length;
        }
        catch
        {
            return; // 大小读不到（瞬时 IO 抖动），放行交由后续实搬动作把关
        }
        if (actualLength != item.FileSize)
            throw new BusinessException("目标文件与归档记录不符（可能已被新版本替换），已取消操作");
    }

    /// <summary>归档解除（撤销 / 退回）时清理该媒体的字幕下载记录</summary>
    /// <remarks>
    /// Subtitle_Download 记的是「下载落地到归档位旁」的字幕。归档一旦解除，这些记录必然悬空：
    /// MOVE_BACK 后字幕随归档视频移回源位、沦为普通 sidecar（系统不再辨识其下载来源）；
    /// DELETE_COPY 后字幕已随归档副本一并删除。两种情形记录的 FileName/TargetPath 都不再指向有效落点，
    /// 故随归档解除一并删除（与「删 MediaItem 时 CASCADE 清字幕记录」同口径）。生产侧原本只 Add 不 Remove，
    /// 此处补上 Remove 出口。**与状态变更同一 SaveChanges 提交**：落库失败由调用方回滚文件 + 事务，
    /// 不会出现「文件已回滚但记录已删」。仅 Completed 期间才会产生下载记录，故对非 Completed 记录为空操作。
    /// </remarks>
    private async Task RemoveSubtitleDownloadsOnUnwindAsync(PmmDbContext db, long mediaItemId, CancellationToken ct)
    {
        List<SubtitleDownload> rows = await db.SubtitleDownloads
            .Where(d => d.MediaItemId == mediaItemId)
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        db.SubtitleDownloads.RemoveRange(rows);
        _logger.LogInformation("归档解除：清理 {Count} 条字幕下载记录（指向的下载字幕已移回源位或随副本删除）：MediaItemId={Id}",
            rows.Count, mediaItemId);
    }

    /// <summary>撤销归档失败补偿：状态落库失败时，把已移回源位的文件再移回归档位，恢复撤销前的一致状态。</summary>
    /// <remarks>
    /// 仅 MOVE_BACK 可补偿（DELETE_COPY 副本已删无法恢复，但源文件始终保留、内容不丢，仅记告警）。
    /// 用 CancellationToken.None：补偿是恢复一致性的关键动作，不应被取消打断。
    /// </remarks>
    private async Task TryRollbackUndoFileAsync(string operation, string sourcePath, string targetPath)
    {
        if (operation != "MOVE_BACK") return;
        try
        {
            // 仅在「源位有文件、归档位为空」时回滚，避免误覆盖归档位上其它文件
            if (_fileProbe.FileExists(sourcePath) && !_fileProbe.FileExists(targetPath))
            {
                await _fileMover.MoveAsync(sourcePath, targetPath, CancellationToken.None);
                RelocateSidecarsBack(sourcePath, targetPath); // 反转字幕移回（source→target）
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销归档补偿失败：文件可能停留在源位、DB 仍为 Completed，需人工核对：{Source}→{Target}", sourcePath, targetPath);
        }
    }

    /// <summary>读最近一条 Archiving 时间线步骤的 operation 是否为 COPY（缺失 / 非 COPY → false，即按 Move 撤销）</summary>
    private static async Task<bool> WasLastArchiveCopyAsync(PmmDbContext db, long mediaItemId, CancellationToken ct)
    {
        string? detail = await db.ProcessSteps.AsNoTracking()
            .Where(s => s.MediaItemId == mediaItemId && s.Stage == MediaItemStatus.Archiving && s.Detail != null)
            .OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id)
            .Select(s => s.Detail)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(detail)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(detail);
            return doc.RootElement.TryGetProperty("operation", out JsonElement op)
                && op.ValueKind == JsonValueKind.String
                && string.Equals(op.GetString(), "COPY", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>撤销 Move：归档目录下与归档视频同 stem 的字幕移回源目录（还原原文件名前缀），并删本次生成的 .nfo</summary>
    /// <remarks>tvshow.nfo / poster.jpg 可能整剧 / 整目录共用，不动；字幕逐条容错（单条失败仅 Warning，不阻断撤销主流程）。</remarks>
    private void RelocateSidecarsBack(string targetVideoPath, string sourceVideoPath)
    {
        string? targetDir = Path.GetDirectoryName(targetVideoPath);
        string? sourceDir = Path.GetDirectoryName(sourceVideoPath);
        if (targetDir is null || sourceDir is null) return;
        string targetStem = Path.GetFileNameWithoutExtension(targetVideoPath);
        string sourceStem = Path.GetFileNameWithoutExtension(sourceVideoPath);

        // 删本次生成的 episode / movie .nfo（{targetStem}.nfo）
        TryDeleteFile(Path.Combine(targetDir, $"{targetStem}.nfo"));

        // 字幕移回：归档目录里以 targetStem 为前缀的字幕 → 还原成 sourceStem 前缀移回源目录（反转归档时的 stem 改名）
        foreach (string sub in EnumerateStemSubtitles(targetDir, targetStem))
        {
            try
            {
                string suffix = Path.GetFileName(sub)[targetStem.Length..]; // 含语言后缀 + 扩展名，如 ".zh.srt"
                string restored = Path.Combine(sourceDir, sourceStem + suffix);
                if (!File.Exists(restored))
                    File.Move(sub, restored);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "撤销归档：字幕移回失败（跳过单条）：{Sub}", sub);
            }
        }
    }

    /// <summary>撤销 Copy：删除归档目录下本次产生的副本（主视频 + 同 stem 字幕 + .nfo）；源文件保留、共用元数据不动</summary>
    private void DeleteArchivedArtifacts(string targetVideoPath)
    {
        TryDeleteFile(targetVideoPath);
        string? targetDir = Path.GetDirectoryName(targetVideoPath);
        if (targetDir is null) return;
        string targetStem = Path.GetFileNameWithoutExtension(targetVideoPath);
        TryDeleteFile(Path.Combine(targetDir, $"{targetStem}.nfo"));
        foreach (string sub in EnumerateStemSubtitles(targetDir, targetStem))
            TryDeleteFile(sub);
    }

    /// <summary>枚举目录下以 stem 为前缀的字幕 / 字幕压缩包（与 ArchiveService.EnumerateSubtitles 同口径）</summary>
    private static IReadOnlyList<string> EnumerateStemSubtitles(string dir, string stem)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        List<string> list = new();
        foreach (string path in Directory.EnumerateFiles(dir))
        {
            string ext = Path.GetExtension(path);
            if (!SubtitleRenamer.SupportedExtensions.Contains(ext) && !SubtitleRenamer.ArchiveExtensions.Contains(ext))
                continue;
            string fileStem = Path.GetFileNameWithoutExtension(path);
            if (fileStem.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                list.Add(path);
        }
        return list;
    }

    /// <summary>删除文件（容错：不存在跳过、失败仅 Warning，不抛）</summary>
    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "撤销归档：删除文件失败（跳过）：{Path}", path); }
    }

    public async Task<BatchRescanResult> RescanAllFailedAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<MediaItem> failed = await db.MediaItems
            .Where(m => m.Status == MediaItemStatus.Failed)
            .ToListAsync(ct);

        // 监控目录列表循环外一次性预载，循环内走纯内存最长前缀匹配（避免每条失败项各查一次全表）
        IReadOnlyList<(long Id, string Path)> watchRoots = await LoadWatchFolderRootsAsync(db, ct);

        // 先批量改状态（一次 SaveChanges），再逐个入队：源文件缺失的跳过
        List<(string SourcePath, long WatchFolderId)> pending = new();
        int skipped = 0;
        foreach (MediaItem item in failed)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(item.SourcePath))
            {
                skipped++;
                continue;
            }
            item.Transition(MediaItemStatus.Queued);
            item.ClearError();
            long watchFolderId = ResolveWatchFolderIdInMemory(item.SourcePath, watchRoots);
            pending.Add((item.SourcePath, watchFolderId));
        }

        await db.SaveChangesAsync(ct);

        int requeued = 0;
        foreach ((string sourcePath, long watchFolderId) in pending)
        {
            try
            {
                await _queue.EnqueueAsync(
                    new Application.Common.PendingFileItem(sourcePath, watchFolderId, Application.Common.PendingFileSource.Manual), ct);
                requeued++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单条入队失败不中断整批；状态已置 Queued 的孤儿由 StartupRecoveryWorker 下次启动兜底
                _logger.LogError(ex, "批量重投入队失败（跳过单条）：{Path}", sourcePath);
            }
        }

        _logger.LogInformation("失败批量重试完成：Requeued={Requeued}, Skipped={Skipped}", requeued, skipped);
        return new BatchRescanResult(requeued, skipped);
    }

    // ---------- 重新处理 / 删除（单条 / 批量 / 整剧统一入口）----------

    /// <summary>单次批量上限：整剧多季合并时也够用，超限提示用户分批</summary>
    private const int HistoryBatchMaxItems = 500;

    public async Task<HistoryBatchResult> ReprocessAsync(HistoryBatchRequest req, CancellationToken ct = default)
    {
        IReadOnlyList<long> ids = await ResolveTargetIdsAsync(req, ct);

        List<long> succeeded = new();
        List<HistoryBatchFailure> failed = new();
        foreach (long id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReprocessOneAsync(id, ct);
                succeeded.Add(id);
            }
            catch (BusinessException ex)
            {
                failed.Add(new HistoryBatchFailure(id, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新处理单条异常：Id={Id}", id);
                failed.Add(new HistoryBatchFailure(id, "重新处理失败"));
            }
        }

        _logger.LogInformation("重新处理完成：Total={Total}, Succeeded={Ok}, Failed={Fail}",
            ids.Count, succeeded.Count, failed.Count);
        return new HistoryBatchResult(ids.Count, succeeded, failed);
    }

    public async Task<HistoryBatchResult> DeleteAsync(HistoryBatchRequest req, CancellationToken ct = default)
    {
        IReadOnlyList<long> ids = await ResolveTargetIdsAsync(req, ct);

        List<long> succeeded = new();
        List<HistoryBatchFailure> failed = new();
        foreach (long id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteOneAsync(id, ct);
                succeeded.Add(id);
            }
            catch (BusinessException ex)
            {
                failed.Add(new HistoryBatchFailure(id, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除记录单条异常：Id={Id}", id);
                failed.Add(new HistoryBatchFailure(id, "删除失败"));
            }
        }

        _logger.LogInformation("删除记录完成：Total={Total}, Succeeded={Ok}, Failed={Fail}",
            ids.Count, succeeded.Count, failed.Count);
        return new HistoryBatchResult(ids.Count, succeeded, failed);
    }

    public async Task<HistoryBatchResult> BatchReopenAsync(HistoryBatchRequest req, CancellationToken ct = default)
    {
        IReadOnlyList<long> ids = await ResolveTargetIdsAsync(req, ct);

        List<long> succeeded = new();
        List<HistoryBatchFailure> failed = new();
        // 逐条复用单条 ReopenForReviewAsync：每条内部各自 CreateDbContextAsync + 完整文件移回事务，
        // 串行 foreach 不并发同一 DbContext（守 EF 红线）；单条失败收进 failed 不阻断其余。
        foreach (long id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReopenForReviewAsync(id, ct);
                succeeded.Add(id);
            }
            catch (BusinessException ex)
            {
                failed.Add(new HistoryBatchFailure(id, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量退回单条异常：Id={Id}", id);
                failed.Add(new HistoryBatchFailure(id, "退回失败"));
            }
        }

        _logger.LogInformation("批量退回完成：Total={Total}, Succeeded={Ok}, Failed={Fail}",
            ids.Count, succeeded.Count, failed.Count);
        return new HistoryBatchResult(ids.Count, succeeded, failed);
    }

    public async Task<HistoryBatchResult> BatchUndoArchiveAsync(HistoryBatchRequest req, CancellationToken ct = default)
    {
        IReadOnlyList<long> ids = await ResolveTargetIdsAsync(req, ct);

        List<long> succeeded = new();
        List<HistoryBatchFailure> failed = new();
        // 逐条复用单条 UndoArchiveAsync（独立 DbContext + 文件移回 / 删副本事务），串行执行守 EF 红线
        foreach (long id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await UndoArchiveAsync(id, ct);
                succeeded.Add(id);
            }
            catch (BusinessException ex)
            {
                failed.Add(new HistoryBatchFailure(id, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量撤销单条异常：Id={Id}", id);
                failed.Add(new HistoryBatchFailure(id, "撤销失败"));
            }
        }

        _logger.LogInformation("批量撤销归档完成：Total={Total}, Succeeded={Ok}, Failed={Fail}",
            ids.Count, succeeded.Count, failed.Count);
        return new HistoryBatchResult(ids.Count, succeeded, failed);
    }

    /// <summary>解析批量目标：Ids 优先（去重保序），否则按 TmdbId 展开该剧全部终态记录</summary>
    private async Task<IReadOnlyList<long>> ResolveTargetIdsAsync(HistoryBatchRequest req, CancellationToken ct)
    {
        if (req.Ids is { Count: > 0 })
        {
            List<long> distinct = req.Ids.Distinct().ToList();
            if (distinct.Count > HistoryBatchMaxItems)
                throw new BusinessException($"单次最多处理 {HistoryBatchMaxItems} 条");
            return distinct;
        }

        if (req.TmdbId is int tmdbId && tmdbId > 0)
        {
            await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            IQueryable<MediaItem> q = db.MediaItems.AsNoTracking()
                .Where(m => m.TmdbId == tmdbId && MediaItemStatusGroups.Terminal.Contains(m.Status));
            if (!string.IsNullOrWhiteSpace(req.TmdbMediaType))
            {
                string mt = req.TmdbMediaType!.Trim().ToLowerInvariant();
                q = q.Where(m => m.TmdbMediaType == mt);
            }

            List<long> ids = await q.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(ct);
            if (ids.Count == 0)
                throw new BusinessException("该剧集下没有可操作的记录");
            if (ids.Count > HistoryBatchMaxItems)
                throw new BusinessException($"该剧集记录数超过单次上限 {HistoryBatchMaxItems}，请改用筛选后分批操作");
            return ids;
        }

        throw new BusinessException("未指定要操作的记录");
    }

    /// <summary>删除单条终态记录（Process_Step 级联删、Audit_AiCall 解绑）；独立 DbContext 隔离单条失败</summary>
    private async Task DeleteOneAsync(long id, CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null) throw new BusinessException("记录不存在");
        if (!item.IsTerminal())
            throw new BusinessException("记录正在处理或待确认中，暂不能删除");

        db.MediaItems.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>重新处理单条：删旧记录后以全新检测重投源文件；独立 DbContext 隔离单条失败</summary>
    /// <remarks>
    /// 删后重投——ProcessFileService 按 SourcePath 找不到旧记录即 CreateDetected 全新建项，
    /// 故得到彻底的全新解析归档，规避「同 SourcePath 已存在 → 扫描跳过」的重入死锁。
    /// 删记录后重投失败（内存 channel 满 / 取消）：源文件仍在，下次扫描会作为新文件重新拾取，不会丢。
    /// </remarks>
    private async Task ReprocessOneAsync(long id, CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null) throw new BusinessException("记录不存在");
        if (!item.IsTerminal())
            throw new BusinessException("记录正在处理或待确认中，无需重新处理");
        // 已归档完成且目标文件仍在（典型：Copy 归档源副本仍存）：删记录重投必然在归档步撞同名冲突
        // → 降级 Skipped（媒体库视图凭空掉一条 Completed），KeepBoth 策略下还会产出重复文件。
        // 引导用户先「撤销归档」（搬回 / 删归档副本）再重新处理；目标已被外部删除的则照常放行。
        if (item.Status == MediaItemStatus.Completed
            && !string.IsNullOrWhiteSpace(item.TargetPath)
            && _fileProbe.FileExists(item.TargetPath))
        {
            throw new BusinessException("该记录已归档且目标文件仍在；如需重新整理，请先撤销归档再重新处理");
        }
        if (!File.Exists(item.SourcePath))
            throw new BusinessException("源文件已不存在，无法重新处理");

        string sourcePath = item.SourcePath;
        // 反查 watchRoot 归属（同 RescanAsync），保证重投后 RelativeSegments 携带「监控根→文件」完整中间层目录
        long watchFolderId = await TryResolveWatchFolderIdAsync(sourcePath, db, ct);

        db.MediaItems.Remove(item);
        await db.SaveChangesAsync(ct);

        try
        {
            await _queue.EnqueueAsync(
                new Application.Common.PendingFileItem(sourcePath, watchFolderId,
                    Application.Common.PendingFileSource.Manual), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新处理重投入队失败（记录已删，可对该文件手动整理）：{Path}", sourcePath);
            throw new BusinessException("重投失败，记录已删除，请对该文件手动整理");
        }
    }

    public async Task<NfoDownloadResult> GetNfoAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? item = await db.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) throw new BusinessException("记录不存在");

        if (string.IsNullOrWhiteSpace(item.TargetPath))
            throw new BusinessException("该记录尚未归档，无 .nfo");

        string nfoPath = Path.ChangeExtension(item.TargetPath, ".nfo");
        if (!File.Exists(nfoPath))
            throw new BusinessException(".nfo 文件不存在（可能未生成元数据或已被外部删除）");

        byte[] bytes = await File.ReadAllBytesAsync(nfoPath, ct);
        string fileName = Path.GetFileName(nfoPath);
        return new NfoDownloadResult(bytes, fileName);
    }

    public async Task<string?> GetPosterPathAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        int? tmdbId = await db.MediaItems.AsNoTracking()
            .Where(m => m.Id == mediaItemId)
            .Select(m => m.TmdbId)
            .FirstOrDefaultAsync(ct);
        if (tmdbId is null or <= 0) return null;

        string path = Path.Combine(_paths.PostersDir, $"{tmdbId.Value}.jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>按 SourcePath 反查 WatchFolderId（前缀匹配 + 最长优先），找不到回退 0</summary>
    /// <remarks>
    /// 用「目录段边界」匹配避免 `F:\迅雷` 误命中 `F:\迅雷下载\xxx.mp4`：
    /// 拼上 DirectorySeparatorChar / AltDirectorySeparatorChar 再 StartsWith。
    /// Windows 路径大小写不敏感 → OrdinalIgnoreCase。
    ///
    /// 同时命中多个嵌套监控目录（如 `F:\迅雷下载` 与 `F:\迅雷下载\电影` 都监控）→ 取**最长**前缀，
    /// 与 FileWatcherWorker 实际事件归属一致（具体目录的 Watcher 才会接到文件）。
    ///
    /// 不过滤 Enabled：禁用只代表停了 Watcher 入队，路径归属语义不变 — 即便后续 Rescan，
    /// 也应让 ProcessFileService 拿到正确 watchRoot 算 RelativeSegments。
    /// </remarks>
    private static async Task<long> TryResolveWatchFolderIdAsync(string sourcePath, PmmDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return 0;
        IReadOnlyList<(long Id, string Path)> roots = await LoadWatchFolderRootsAsync(db, ct);
        return ResolveWatchFolderIdInMemory(sourcePath, roots);
    }

    /// <summary>一次性加载监控目录 (Id, Path) 投影；供批量重投循环外预载，避免逐条全表查询</summary>
    private static async Task<IReadOnlyList<(long Id, string Path)>> LoadWatchFolderRootsAsync(PmmDbContext db, CancellationToken ct)
    {
        var rows = await db.WatchFolders.AsNoTracking()
            .Where(w => w.Path != null && w.Path != "")
            .Select(w => new { w.Id, w.Path })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, Path: r.Path!)).ToList();
    }

    /// <summary>纯内存「最长目录前缀」匹配（含目录段边界，避免 F:\迅雷 误命中 F:\迅雷下载）</summary>
    private static long ResolveWatchFolderIdInMemory(string sourcePath, IReadOnlyList<(long Id, string Path)> roots)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return 0;

        long bestId = 0;
        int bestLen = -1;
        foreach ((long id, string path) in roots)
        {
            string root = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (root.Length == 0) continue;

            bool hit =
                sourcePath.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                sourcePath.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (hit && root.Length > bestLen)
            {
                bestId = id;
                bestLen = root.Length;
            }
        }
        return bestId;
    }

    /// <summary>从 ParsedInfo JSON 平铺 title / year / season / episode / episodeEnd（损坏字段降级为 null）</summary>
    internal static (string? title, int? year, int? season, int? episode, int? episodeEnd) ExtractParsedInfo(string? parsedInfo)
    {
        if (string.IsNullOrWhiteSpace(parsedInfo)) return (null, null, null, null, null);
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(parsedInfo);
            System.Text.Json.JsonElement root = doc.RootElement;
            string? title = root.TryGetProperty("title", out System.Text.Json.JsonElement t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString() : null;
            return (title, ReadInt(root, "year"), ReadInt(root, "season"), ReadInt(root, "episode"), ReadInt(root, "episodeEnd"));
        }
        catch (System.Text.Json.JsonException) { return (null, null, null, null, null); }

        static int? ReadInt(System.Text.Json.JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out System.Text.Json.JsonElement v)) return null;
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out int iv)) return iv;
            if (v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), out int sv)) return sv;
            return null;
        }
    }
}
