using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Review;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Review;

/// <summary>人工确认队列服务（D7.5）实现</summary>
/// <remarks>
/// 状态机硬约束：所有写操作前置校验 Status==AwaitingReview，否则抛 BusinessException「该记录不在待确认状态」。
/// 乐观并发：所有写操作比对客户端传入的 RowVersion 与 DB 当前值，不一致抛 BusinessException「记录已被其他用户修改，请刷新」。
/// confirm 后调 IArchiveService.ArchiveAsync 同步等待归档完成；失败回滚为 Failed 状态并抛 9000 类业务错。
/// confirm 归档与自动 / 手动入口同口径：落 Archiving（operation=MOVE）+ 终态时间线步骤、arc.Warnings 非空时
///   标记「待补元数据」并告警；冲突跳过发 media.skipped、失败发 media.failed（media.archived 由 ArchiveService 统一发）。
/// 失败补偿（MarkFailedSafelyAsync）落库一律用 CancellationToken.None：用户中断（请求取消）也必须完成
///   Failed 收尾，绝不让记录停留在 Archiving 非终态。
/// confirm / bind-tmdb 更正绑定后失效同目录 series 复用缓存（IFolderSeriesCache.Remove），防后续文件复用旧错误 tmdbId。
///
/// tmdb-search：不限制 MediaItem 必须在 AwaitingReview（任意状态都可重查 TMDB 帮用户复核），
///   仅校验 MediaItem 存在。
/// bind-tmdb：要求 AwaitingReview；不改 status，仅更新 TmdbId/TmdbMediaType + 拉详情写缓存（GetDetailsAsync 自动写）。
/// </remarks>
internal sealed class ReviewService : IReviewService
{
    private const int ListMaxPageSize = 100;
    // items 集合的「非空 / 数量上限」校验已上移至各批量 Request DTO（[Required]+[MinLength(1)]+[MaxLength(N)]，N=50/500）
    private const string FallbackExtension = "mkv"; // 文件名无扩展名时的占位（与 DryRunService 一致）
    private const string TmdbImageBase = "https://image.tmdb.org/t/p/w500";

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly ITmdbSearchService _tmdb;
    private readonly IArchiveService _archive;
    private readonly IFileProbe _fileProbe;
    private readonly IFolderSeriesCache _folderCache;
    private readonly IWebhookEmitter _webhook;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        IDbContextFactory<PmmDbContext> dbFactory,
        ITmdbSearchService tmdb,
        IArchiveService archive,
        IFileProbe fileProbe,
        IFolderSeriesCache folderCache,
        IWebhookEmitter webhook,
        ILogger<ReviewService> logger)
    {
        _dbFactory = dbFactory;
        _tmdb = tmdb;
        _archive = archive;
        _fileProbe = fileProbe;
        _folderCache = folderCache;
        _webhook = webhook;
        _logger = logger;
    }

    /// <summary>时间线步骤 JSON 配置（camelCase、省 null、CJK 不转义，与 ProcessFileService / HistoryService 一致）</summary>
    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string SerializeStep(object detail) => JsonSerializer.Serialize(detail, StepJsonOptions);

    // ---------- List ----------

    public async Task<ReviewListPage> ListAsync(ReviewListQuery query, CancellationToken ct = default)
    {
        // page 范围校验（page ≥ 1）已上移至 ReviewListQuery DataAnnotations，模型绑定阶段拦截
        int pageSize = query.PageSize <= 0 ? 20 : Math.Min(query.PageSize, ListMaxPageSize);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<MediaItem> q = db.MediaItems.AsNoTracking()
            .Where(m => m.Status == MediaItemStatus.AwaitingReview);
        if (query.ParseSource is not null)
        {
            q = q.Where(m => m.ParseSource == query.ParseSource);
        }

        int total = await q.CountAsync(ct);
        List<MediaItem> rows = await q
            .OrderByDescending(m => m.Id)
            .Skip((query.Page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        // 拉对应 TmdbMetadataCache 用于 candidates 展示（每个 item 取自己当前绑定的 TmdbId 那一条）
        int[] tmdbIds = rows.Where(r => r.TmdbId is not null).Select(r => r.TmdbId!.Value).Distinct().ToArray();
        List<TmdbMetadataCache> caches = tmdbIds.Length == 0
            ? new()
            : await db.TmdbMetadataCaches.AsNoTracking()
                .Where(c => tmdbIds.Contains(c.TmdbId))
                .ToListAsync(ct);

        IReadOnlyList<ReviewItemResponse> items = rows.Select(r =>
        {
            // 候选优先取解析阶段持久化的快照全集（多候选场景）；历史数据无快照时回退到当前绑定 TmdbId 的元数据缓存。
            // 快照候选本身不含 TotalSeasons（来自 search 而非 details）：已缓存详情的候选（多为默认绑定的 top）
            // 从已加载的 caches 即时回填，让审核页季号下拉就绪；未命中缓存者置 null，前端选中时再懒查 tmdb-detail 补全。
            IReadOnlyList<ReviewCandidateSnapshot> snapshots = ReviewCandidateSnapshot.Deserialize(r.TmdbCandidatesJson);
            IReadOnlyList<ReviewTmdbCandidate> candidates;
            if (snapshots.Count > 0)
            {
                candidates = snapshots.Select(s => new ReviewTmdbCandidate(
                    s.TmdbId, s.MediaType, s.Title, s.OriginalTitle, s.Year, ToPosterUrl(s.PosterPath),
                    caches.FirstOrDefault(c => c.TmdbId == s.TmdbId
                        && string.Equals(c.MediaType, s.MediaType, StringComparison.OrdinalIgnoreCase))?.TotalSeasons))
                    .ToList();
            }
            else if (r.TmdbId is not null)
            {
                candidates = caches.Where(c => c.TmdbId == r.TmdbId && c.MediaType == r.TmdbMediaType)
                    .Select(c => new ReviewTmdbCandidate(c.TmdbId, c.MediaType, c.Title, c.OriginalTitle, c.Year, ToPosterUrl(c.PosterPath), c.TotalSeasons))
                    .ToList();
            }
            else
            {
                candidates = Array.Empty<ReviewTmdbCandidate>();
            }
            return new ReviewItemResponse(
                r.Id, r.FileName, r.SourcePath, r.ParseSource, r.Confidence,
                r.ParsedInfo, candidates, r.RowVersion, r.CreatedAt, r.ReviewReason, r.AiInvolved);
        }).ToList();

        return new ReviewListPage(items, total, query.Page, pageSize);
    }

    // ---------- Confirm ----------

    public async Task<ConfirmResult> ConfirmAsync(long mediaItemId, ConfirmRequest req, CancellationToken ct = default)
    {
        ValidateMediaType(req.MediaType);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem item = await LoadAwaitingReviewWithConcurrencyAsync(db, mediaItemId, req.RowVersion, ct);

        // 同名冲突待确认项（NameCollision）的「确认归档」即人工裁定覆盖：传 ForceOverwrite 无条件覆盖目标已存在文件
        // （删旧 + 清旧伴生字幕/nfo + 失效旧记录）。其它原因（多候选 / 分类未定 等）仍遵循系统 Archive_ConflictPolicy。
        ArchiveConflictResolution conflictResolution = item.ReviewReason == ReviewReason.NameCollision
            ? ArchiveConflictResolution.ForceOverwrite
            : ArchiveConflictResolution.FollowPolicy;

        CategoryDefinition? cat = await db.CategoryDefinitions
            .FirstOrDefaultAsync(c => c.Id == req.CategoryId, ct);
        if (cat is null) throw new BusinessException("分类不存在");

        // 拉一次详情确认 tmdbId 有效（同时填入缓存供 Archive 用），并取总季数用于单季自动补季
        int? totalSeasons;
        try
        {
            TmdbDetailsResult? details = await _tmdb.GetDetailsAsync(req.TmdbId, req.MediaType.ToLowerInvariant(), ct);
            totalSeasons = details?.TotalSeasons;
        }
        catch (TmdbClientException ex)
        {
            throw new BusinessException("TMDB ID 无效或无法查询", ex);
        }

        // 剧集季集校验（含单季自动补季）：缺季号但有集号、且 TMDB 仅 1 季 → 默认第 1 季；多季 / 缺集号仍要求人工补全
        bool isTv = string.Equals(req.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        int? effectiveSeason = req.Season;
        if (isTv && effectiveSeason is null && req.Episode is not null && totalSeasons == 1)
        {
            effectiveSeason = 1;
        }
        if (isTv && (effectiveSeason is null || req.Episode is null))
        {
            throw new BusinessException("剧集必须填写季号与集号");
        }

        // 应用用户覆盖（title/year/season/episode）到 ParsedInfo；空字段保留解析原值
        ParsedInfo merged = (ParsedInfo.FromJson(item.ParsedInfo)
                ?? ParsedInfo.CreateFromOverride(null, null, req.MediaType, null, null))
            .MergeWith(req.Title, req.Year, req.MediaType, effectiveSeason, req.Episode);
        // EpisodeEnd 以确认表单为权威（null = 用户清空末集 → 显式清除旧值）：
        // MergeWith 的「null 视为未提供、保留原值」语义会把清空静默吞掉，导致去向预览（直接用表单值）显示单集、
        // 实际归档却带回解析阶段的 stale 区间（SxxEaa-Ebb），预览 ≠ 实际落点。
        merged = merged with { EpisodeEnd = req.EpisodeEnd };
        if (!isTv)
        {
            // 电影确认：顺带清掉解析阶段残留的季 / 集字段（电影无季集语义，残留会污染展示与后续归档判断）
            merged = merged with { Season = null, Episode = null, EpisodeEnd = null };
        }
        item.ApplyManualMatch(req.TmdbId, req.MediaType.ToLowerInvariant(), req.CategoryId, merged);

        item.Transition(MediaItemStatus.Archiving);
        await db.SaveChangesAsync(ct);
        long rowAfterConfirm = item.RowVersion;

        // 确认即用户对 TMDB 绑定的最终裁决：先失效同目录 series 复用缓存（L1）防旧错误条目残留，
        // 再把人工裁决的绑定主动沉淀回缓存（仅 TV）——同目录后续文件（典型如周更剧新集）直接复用
        // 人工结果，免搜索免 AI 也免再次人工；不等 L2（已归档兄弟集）查库回填，也不受专属子目录限制。
        InvalidateFolderCache(item.SourcePath);
        if (isTv)
        {
            SeedFolderCache(item.SourcePath, req.TmdbId, "tv", merged.Title, merged.Year);
        }

        // 同步等归档；任何异常 → MarkFailed + 抛 9000；取消也先完成 Failed 收尾再透传（绝不留 Archiving）
        DateTimeOffset archiveStartedAt = DateTimeOffset.UtcNow;
        try
        {
            // 普通确认（FollowPolicy）走原 2 参重载，行为与改动前一字不差（兼容既有调用方 / 测试桩）；
            // 仅 NameCollision 人工裁定覆盖（ForceOverwrite）走显式冲突处理重载，无条件覆盖目标已存在文件。
            ArchiveResult arc = conflictResolution == ArchiveConflictResolution.ForceOverwrite
                ? await _archive.ArchiveAsync(item, ArchiveOperation.Move, conflictResolution, ct)
                : await _archive.ArchiveAsync(item, ct);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long durMs = (long)Math.Max(0d, (now - archiveStartedAt).TotalMilliseconds);

            if (arc.Outcome == ArchiveOutcome.ConflictPending)
            {
                // 询问策略下确认归档仍同名冲突（且本项非 NameCollision、未走强制覆盖）：退回待确认队列标 NameCollision，
                // 让用户改选「覆盖」或「忽略」。不写 TargetPath（冲突目标是他人产物）；Archiving → AwaitingReview。
                item.AppendStep(MediaItemStatus.Archiving, archiveStartedAt, durMs, SerializeStep(new
                {
                    operation = "MOVE",
                    source = item.SourcePath,
                    target = arc.TargetPath,
                    conflict = true,
                    decision = "同名冲突 → 退回待确认（询问策略）",
                    confirm = true,
                }));
                item.MarkAwaitingReview(ReviewReason.NameCollision);
                item.AppendStep(MediaItemStatus.AwaitingReview, now, durMs: 0, SerializeStep(new
                {
                    reason = "目标已存在同名文件，待人工确认是否覆盖",
                    conflictTarget = arc.TargetPath,
                    confirm = true,
                }));
                // 状态写入用 CancellationToken.None：与下方终态落库同口径，绝不让记录停留在 Archiving 非终态
                await db.SaveChangesAsync(CancellationToken.None);
                await EmitReviewCreatedAsync(item, ct);
                return new ConfirmResult(item.Id, MediaItemStatus.AwaitingReview, item.RowVersion);
            }

            item.SetArchiveResult(arc.TargetPath);
            // 视频已落地但 nfo / Webhook 失败时（arc.Warnings 非空），时间线标记「待补元数据」，仍判 Completed 不回退
            // —— 与自动管线（ProcessFileService）/ 手动移动（HistoryService.ManualArchiveAsync）字段口径完全一致
            bool metadataPending = arc.Warnings is { Count: > 0 };
            // 时间线补归档步骤；operation=MOVE 同时是「撤销归档」判定当初 Move/Copy 的依据（确认归档固定移动），
            // 缺这一步会让 WasLastArchiveCopyAsync 读到更早的无关步骤、撤销方式判定依赖巧合
            item.AppendStep(MediaItemStatus.Archiving, archiveStartedAt, durMs, SerializeStep(new
            {
                operation = "MOVE",
                source = item.SourcePath,
                target = arc.TargetPath,
                confirm = true,
                metadataPending = metadataPending ? true : (bool?)null, // 省 null：无待补时不落该字段
                warnings = metadataPending ? arc.Warnings : null,
            }));

            MediaItemStatus next = arc.Outcome == ArchiveOutcome.Completed
                ? MediaItemStatus.Completed
                : MediaItemStatus.Skipped;
            item.Transition(next);
            item.AppendStep(next, now, durMs: 0, next == MediaItemStatus.Completed
                ? SerializeStep(new { target = arc.TargetPath, confirm = true })
                : SerializeStep(new { reason = "目标已存在同名文件（确认归档冲突跳过）", confirm = true }));
            // 终态落库用 CancellationToken.None：文件已实际移动，此刻被取消打断会让内存终态与 DB 的 Archiving
            // 永久分叉（终态进不了 MarkFailed 补偿），状态写入是恢复一致性的关键动作、不应被取消
            await db.SaveChangesAsync(CancellationToken.None);

            if (metadataPending)
                _logger.LogWarning("确认归档完成但元数据待补：{Source} → {Target}（待补：{Warnings}）",
                    item.SourcePath, arc.TargetPath, string.Join("；", arc.Warnings!));

            // 冲突跳过旁路与自动管线对齐：发 media.skipped（media.archived 由 ArchiveService 统一发，此处不重复发）
            if (next == MediaItemStatus.Skipped)
                await EmitSkippedAsync(item, "目标已存在同名文件", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户中断（请求取消 / 关停）：先把记录收尾为可恢复终态 Failed（补偿内部用 CancellationToken.None
            // 落库，不被已取消的 ct 打断），再透传取消 —— 目标：任何中断后记录不得停留在 Archiving
            await MarkFailedSafelyAsync(db, item, "确认归档被中断（请求取消）");
            throw;
        }
        catch (BusinessException)
        {
            // ArchiveService 抛的业务异常（如缺字段）— 透传，但先把 item 拉回 Failed
            await MarkFailedSafelyAsync(db, item, "归档失败：业务校验未通过");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "确认归档失败：MediaItemId={MediaItemId}", mediaItemId);
            await MarkFailedSafelyAsync(db, item, ex.Message);
            throw new BusinessException("提交归档任务失败", ex);
        }

        // 按 API 规范返回 confirm 那一刻的快照（Archiving + 当时 rowVersion）
        return new ConfirmResult(item.Id, MediaItemStatus.Archiving, rowAfterConfirm);
    }

    // ---------- Ignore ----------

    public async Task<IgnoreResult> IgnoreAsync(long mediaItemId, IgnoreRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem item = await LoadAwaitingReviewWithConcurrencyAsync(db, mediaItemId, req.RowVersion, ct);

        // AwaitingReview → Ignored 是合法转移
        item.Transition(MediaItemStatus.Ignored);
        if (!string.IsNullOrWhiteSpace(req.Reason))
        {
            // 保留用户原因到 ErrorMessage 字段（仅作日志线索）
            item.RecordError($"用户忽略：{req.Reason}");
        }
        // 人工操作落时间线：让详情页可追溯「谁的决定把记录移出了待确认队列」
        item.AppendStep(MediaItemStatus.Ignored, DateTimeOffset.UtcNow, durMs: 0, SerializeStep(new
        {
            reason = string.IsNullOrWhiteSpace(req.Reason) ? "用户在审核页忽略" : $"用户在审核页忽略：{req.Reason}",
            manual = true,
        }));
        await db.SaveChangesAsync(ct);

        return new IgnoreResult(item.Id, item.Status);
    }

    // ---------- Batch Confirm ----------

    public async Task<BatchConfirmResult> BatchConfirmAsync(BatchConfirmRequest req, CancellationToken ct = default)
    {
        List<long> succeeded = new();
        List<BatchConfirmFailure> failed = new();

        foreach (BatchConfirmItem entry in req.Items)
        {
            try
            {
                ConfirmRequest single = new(
                    entry.TmdbId, entry.MediaType, entry.CategoryId,
                    entry.Title, entry.Year, entry.Season, entry.Episode, entry.RowVersion, entry.EpisodeEnd);
                await ConfirmAsync(entry.Id, single, ct);
                succeeded.Add(entry.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 请求已取消：当前条已在 ConfirmAsync 内完成 Failed 收尾（不会卡 Archiving），
                // 其余未处理条保持 AwaitingReview 原状；停止整批并透传取消（继续循环只会逐条立即取消、徒增噪声）
                throw;
            }
            catch (BusinessException ex)
            {
                failed.Add(new BatchConfirmFailure(entry.Id, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量确认单条异常：Id={Id}", entry.Id);
                failed.Add(new BatchConfirmFailure(entry.Id, "提交归档任务失败"));
            }
        }

        return new BatchConfirmResult(succeeded, failed);
    }

    // ---------- Batch Ignore ----------

    public async Task<BatchIgnoreResult> BatchIgnoreAsync(BatchIgnoreRequest req, CancellationToken ct = default)
    {
        List<long> succeeded = new();
        List<BatchIgnoreFailure> failed = new();

        foreach (BatchIgnoreItem entry in req.Items)
        {
            try
            {
                IgnoreRequest single = new(entry.RowVersion, req.Reason);
                await IgnoreAsync(entry.Id, single, ct);
                succeeded.Add(entry.Id);
            }
            catch (BusinessException ex)
            {
                failed.Add(new BatchIgnoreFailure(entry.Id, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量忽略单条异常：Id={Id}", entry.Id);
                failed.Add(new BatchIgnoreFailure(entry.Id, "忽略失败"));
            }
        }

        return new BatchIgnoreResult(succeeded, failed);
    }

    // ---------- TMDB Search ----------

    public async Task<TmdbSearchListResult> TmdbSearchAsync(long mediaItemId, TmdbSearchListQuery query, CancellationToken ct = default)
    {
        // 搜索词非空校验已上移至 TmdbSearchListQuery DataAnnotations，模型绑定阶段拦截
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        bool exists = await db.MediaItems.AsNoTracking().AnyAsync(m => m.Id == mediaItemId, ct);
        if (!exists) throw new BusinessException("记录不存在");

        string type = query.Type?.ToLowerInvariant() switch
        {
            "movie" => "movie",
            "tv" => "tv",
            _ => "unknown", // both → unknown 让 TmdbSearchService 两个都查
        };

        TmdbSearchResult tmdb;
        try
        {
            tmdb = await _tmdb.SearchAsync(new TmdbSearchRequest(query.Query, type, query.Year), ct);
        }
        catch (TmdbClientException ex)
        {
            throw new BusinessException("TMDB 服务异常", ex);
        }

        IReadOnlyList<TmdbSearchListItem> items = tmdb.Candidates.Select(c => new TmdbSearchListItem(
            c.Id, c.MediaType, c.Title, c.OriginalTitle, c.Year,
            ToPosterUrl(c.PosterPath), c.OriginCountry, c.Popularity)).ToList();

        return new TmdbSearchListResult(items);
    }

    // ---------- Bind TMDB ----------

    public async Task<BindTmdbResult> BindTmdbAsync(long mediaItemId, BindTmdbRequest req, CancellationToken ct = default)
    {
        ValidateMediaType(req.MediaType);
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem item = await LoadAwaitingReviewWithConcurrencyAsync(db, mediaItemId, req.RowVersion, ct);

        TmdbDetailsResult details;
        try
        {
            details = await _tmdb.GetDetailsAsync(req.TmdbId, req.MediaType.ToLowerInvariant(), ct);
        }
        catch (TmdbClientException ex)
        {
            throw new BusinessException("TMDB ID 无效或无法查询", ex);
        }

        ParsedInfo rebound = (ParsedInfo.FromJson(item.ParsedInfo)
                ?? ParsedInfo.CreateFromOverride(null, null, req.MediaType, null, null))
            .MergeWith(
                title: details.Title ?? details.OriginalTitle,
                year: details.Year,
                mediaType: req.MediaType,
                season: null,
                episode: null);
        item.RebindTmdb(req.TmdbId, req.MediaType.ToLowerInvariant(), rebound);
        await db.SaveChangesAsync(ct);

        // 用户改绑 TMDB：先失效同目录 series 复用缓存（防旧 tmdbId 残留），再沉淀改绑后的新绑定（仅 TV），
        // 同目录后续文件直接复用人工改绑结果；标题是 TMDB 规范名，与文件名域可能失配时由复用侧标题守门兜底
        InvalidateFolderCache(item.SourcePath);
        if (string.Equals(req.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            SeedFolderCache(item.SourcePath, req.TmdbId, "tv", rebound.Title, rebound.Year);
        }

        return new BindTmdbResult(item.Id, req.TmdbId, details.Title ?? details.OriginalTitle, details.Year, item.RowVersion);
    }

    // ---------- TMDB Detail (按 ID 取详情) ----------

    public async Task<TmdbDetailItem> TmdbDetailAsync(long mediaItemId, TmdbDetailQuery query, CancellationToken ct = default)
    {
        ValidateMediaType(query.MediaType);

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        bool exists = await db.MediaItems.AsNoTracking().AnyAsync(m => m.Id == mediaItemId, ct);
        if (!exists) throw new BusinessException("记录不存在");

        TmdbDetailsResult details;
        try
        {
            details = await _tmdb.GetDetailsAsync(query.TmdbId, query.MediaType.ToLowerInvariant(), ct);
        }
        catch (TmdbClientException ex)
        {
            throw new BusinessException("TMDB ID 无效或无法查询", ex);
        }

        return new TmdbDetailItem(
            details.TmdbId,
            details.MediaType,
            details.Title,
            details.OriginalTitle,
            details.Year,
            ToPosterUrl(details.PosterPath),
            details.TotalSeasons,
            details.OriginCountry,
            details.Seasons?.Select(s => new ReviewSeasonEpisodeCount(s.SeasonNumber, s.EpisodeCount, s.Name)).ToList());
    }

    // ---------- Preview Path (去向预览) ----------

    public async Task<ReviewPreviewPathResult> PreviewPathsAsync(ReviewPreviewPathRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        long[] categoryIds = req.Items.Select(i => i.CategoryId).Distinct().ToArray();
        Dictionary<long, CategoryDefinition> categories = await db.CategoryDefinitions.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        // 加载候选 tmdbId 的规范标题缓存：去向预览的标题段须与实际归档一致（实际归档用 meta.Title），
        // 否则预览展示解析名 / 用户输入名而真实落点用规范名，会误导用户。缓存缺失则回退入参 Title（与实际同回退）。
        int[] tmdbIds = req.Items.Select(i => i.TmdbId).Distinct().ToArray();
        List<TmdbMetadataCache> caches = tmdbIds.Length == 0
            ? new()
            : await db.TmdbMetadataCaches.AsNoTracking().Where(c => tmdbIds.Contains(c.TmdbId)).ToListAsync(ct);

        // 去向预览读同一套归档命名模板（与实际归档逐字节一致；非法 / 缺失回退默认）
        NamingTemplateOptions namingTemplate = await ArchiveNamingTemplateReader.ReadAsync(db, _logger, ct);

        List<ReviewPreviewPathEntry> entries = new(req.Items.Count);
        foreach (ReviewPreviewPathItem it in req.Items)
        {
            entries.Add(BuildPreviewEntry(it, categories, caches, _logger, namingTemplate));
        }
        return new ReviewPreviewPathResult(entries);
    }

    /// <summary>单项去向计算：缺字段 / 命名校验失败时返回带 Error 的条目（不抛异常，整批不因单条短路）</summary>
    /// <remarks>
    /// 标题段优先用 TMDB 规范名（caches 命中），缺失才回退入参 Title；目录按 {tmdb-NNN} 复用分类根已存在目录——
    /// 与实际归档共用 ArchiveFolderResolver，保证预览即确认后真实落点。
    /// </remarks>
    private static ReviewPreviewPathEntry BuildPreviewEntry(
        ReviewPreviewPathItem it,
        IReadOnlyDictionary<long, CategoryDefinition> categories,
        IReadOnlyList<TmdbMetadataCache> caches,
        ILogger? logger,
        NamingTemplateOptions namingTemplate)
    {
        if (!categories.TryGetValue(it.CategoryId, out CategoryDefinition? cat))
            return new ReviewPreviewPathEntry(it.Key, null, null, "分类不存在");

        TmdbMetadataCache? cache = caches.FirstOrDefault(c => c.TmdbId == it.TmdbId
            && string.Equals(c.MediaType, it.MediaType, StringComparison.OrdinalIgnoreCase));
        // 标题语言优先级：TMDB 缓存中文名 → TMDB 缓存原文名 → 审核项标题（与实际归档一致）
        string? title = ArchiveFolderResolver.FirstNonBlank(cache?.Title, cache?.OriginalTitle, it.Title);
        if (string.IsNullOrWhiteSpace(title))
            return new ReviewPreviewPathEntry(it.Key, null, null, "标题缺失");
        if (it.Year is null)
            return new ReviewPreviewPathEntry(it.Key, null, null, "年份缺失，无法按 Plex 命名");

        bool isTv = string.Equals(it.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        if (isTv && (it.Season is null || it.Episode is null))
            return new ReviewPreviewPathEntry(it.Key, null, null, "剧集季 / 集不全");

        string ext = Path.GetExtension(it.FileName ?? string.Empty).TrimStart('.');
        if (string.IsNullOrEmpty(ext)) ext = FallbackExtension;

        // 多季：预览侧用已加载缓存的 RawJson 解析该季首播年 + 季名（预览无文件解析篇章名，传 null），
        // 与实际归档共用 ResolveSeasonNaming，保证预览即真实落点。
        (int? seasonYear, string? seasonTitle) = isTv
            ? ArchiveFolderResolver.ResolveSeasonNaming(cache?.RawJson, null, it.Season!.Value)
            : (null, null);

        string relative;
        try
        {
            relative = ArchiveFolderResolver.BuildRelativeTargetPath(
                title, it.Year.Value, it.TmdbId, isTv,
                it.Season ?? 0, it.Episode ?? 0, it.EpisodeEnd, ext, cat.TargetRoot, logger,
                seasonYear, seasonTitle, namingTemplate, cache?.OriginalTitle);
        }
        catch (ArgumentException ex)
        {
            return new ReviewPreviewPathEntry(it.Key, null, null, $"命名校验未通过：{ex.Message}");
        }

        string full = Path.Combine(cat.TargetRoot, relative);
        return new ReviewPreviewPathEntry(it.Key, relative, full, null);
    }

    // ---------- Check Files (文件存在性检查) ----------

    public async Task<CheckFilesResult> CheckFilesAsync(CheckFilesRequest req, CancellationToken ct = default)
    {
        // 期望 RowVersion 表（同 Id 去重，后值覆盖），用作乐观并发校验
        Dictionary<long, long> expectedRowVersions = new();
        foreach (CheckFileItem entry in req.Items) expectedRowVersions[entry.Id] = entry.RowVersion;
        long[] ids = expectedRowVersions.Keys.ToArray();

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<MediaItem> rows = await db.MediaItems.Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        Dictionary<long, MediaItem> loaded = rows.ToDictionary(m => m.Id);

        List<long> removed = new();
        List<CheckFileFailure> failed = new();
        int kept = 0;

        foreach (long id in ids)
        {
            if (!loaded.TryGetValue(id, out MediaItem? item))
            {
                failed.Add(new CheckFileFailure(id, "记录不存在"));
                continue;
            }
            if (item.Status != MediaItemStatus.AwaitingReview)
            {
                failed.Add(new CheckFileFailure(id, "该记录不在待确认状态"));
                continue;
            }
            if (item.RowVersion != expectedRowVersions[id])
            {
                failed.Add(new CheckFileFailure(id, "记录已被其他用户修改，请刷新"));
                continue;
            }

            if (_fileProbe.FileExists(item.SourcePath))
            {
                kept++;
            }
            else
            {
                // 源文件已不存在：转 Ignored 移出队列（与手动忽略同终态），原因写入 ErrorMessage 供审计
                item.Transition(MediaItemStatus.Ignored);
                item.RecordError("源文件已不存在（文件检查移除）");
                // 人工触发的状态改写落时间线：详情页可追溯本条因何离开待确认队列
                item.AppendStep(MediaItemStatus.Ignored, DateTimeOffset.UtcNow, durMs: 0, SerializeStep(new
                {
                    reason = "源文件已不存在（文件检查移除）",
                    manual = true,
                }));
                removed.Add(id);
            }
        }

        if (removed.Count > 0) await db.SaveChangesAsync(ct);

        _logger.LogInformation("文件检查：移除 {Removed} 条 / 保留 {Kept} 条 / 未处理 {Failed} 条",
            removed.Count, kept, failed.Count);
        return new CheckFilesResult(removed, kept, failed);
    }

    // ---------- helpers ----------

    private static async Task<MediaItem> LoadAwaitingReviewWithConcurrencyAsync(
        PmmDbContext db, long id, long expectedRowVersion, CancellationToken ct)
    {
        MediaItem? item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null) throw new BusinessException("记录不存在");
        if (item.RowVersion != expectedRowVersion)
            throw new BusinessException("记录已被其他用户修改，请刷新");
        if (item.Status != MediaItemStatus.AwaitingReview)
            throw new BusinessException("该记录不在待确认状态");
        return item;
    }

    private static void ValidateMediaType(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            throw new BusinessException("mediaType 取值非法（应为 movie/tv）");
        string m = mediaType.ToLowerInvariant();
        if (m is not "movie" and not "tv")
            throw new BusinessException("mediaType 取值非法（应为 movie/tv）");
    }

    /// <summary>确认归档失败补偿：MarkFailed + 落 Failed 终态时间线 + 发 media.failed（落库一律 CancellationToken.None）</summary>
    /// <remarks>
    /// 补偿是恢复一致性的关键动作，不应被取消打断（同口径参考 HistoryService.TryRollbackUndoFileAsync）：
    /// 调用方的 ct 在用户中断确认 / 批量确认时已是取消态，若沿用则补偿落库立刻抛 OperationCanceledException，
    /// 记录永远停留在 Archiving 非终态 —— 不可 Rescan、不可手动移动，只能等进程重启恢复。
    /// </remarks>
    private async Task MarkFailedSafelyAsync(PmmDbContext db, MediaItem item, string reason)
    {
        try
        {
            if (!item.IsTerminal())
            {
                MediaItemStatus fromStage = item.Status;
                item.MarkFailed(reason);
                // Failed 是终态，补一条时间线步骤让确认失败也有「失败收尾」可查（与自动管线 SafeMarkFailedAsync 口径一致）
                item.AppendStep(MediaItemStatus.Failed, DateTimeOffset.UtcNow, durMs: 0,
                    SerializeStep(new { reason, fromStage = fromStage.ToString() }));
                await db.SaveChangesAsync(CancellationToken.None);
                // 失败旁路与自动管线对齐：发 media.failed（发射器实现侧自吞异常，不阻断补偿主流程）
                await EmitFailedAsync(item, reason, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarkFailed 二次失败：MediaItemId={MediaItemId}", item.Id);
            // P2-r3.8：补偿失败必须向上抛而非静默吞咽
            // 不抛 → 原始 ConfirmAsync 的 catch 链继续 throw BusinessException → 但 MediaItem 留在 Archiving；
            // 抛 → ExceptionHandlerMiddleware 走 9000 ServerError「服务器内部错误」，OPS 立即告警；
            // 数据完整性破坏比业务校验失败严重，必须让最外层中间件感知。
            throw;
        }
    }

    /// <summary>发 media.failed Webhook（确认归档失败终态）；受总开关 gated + 实现侧自吞异常，不阻断主流程</summary>
    /// <remarks>payload 字段口径与自动管线（ProcessFileService.EmitFailedAsync）完全一致。</remarks>
    private Task EmitFailedAsync(MediaItem media, string error, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaFailed, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            error,
        }, ct);

    /// <summary>发 media.skipped Webhook（确认归档同名冲突跳过）；受总开关 gated + 实现侧自吞异常</summary>
    /// <remarks>payload 字段口径与自动管线（ProcessFileService.EmitSkippedAsync）完全一致。</remarks>
    private Task EmitSkippedAsync(MediaItem media, string reason, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaSkipped, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            targetPath = media.TargetPath,
            reason,
        }, ct);

    /// <summary>发 review.created Webhook（退回人工确认队列）；ReviewReason 此刻已由 MarkAwaitingReview 赋值</summary>
    /// <remarks>payload 字段口径与自动管线（ProcessFileService.EmitReviewCreatedAsync）一致；受总开关 gated + 实现侧自吞异常。</remarks>
    private Task EmitReviewCreatedAsync(MediaItem media, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.ReviewCreated, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            reviewReason = media.ReviewReason?.ToString(),
            tmdbId = media.TmdbId,
            type = media.TmdbMediaType,
        }, ct);

    /// <summary>失效同目录 series 复用缓存（L1）；键构造与 ProcessFileService.TryFolderKey 同构（文件父目录完整路径）</summary>
    /// <remarks>
    /// 用户在审核页更正 TMDB 绑定（confirm / bind-tmdb）后调用：L1 内存条目永远优先于 L2 持久层，
    /// 不失效则进程常驻期间同目录后续文件（典型如周更剧）持续复用旧错误 tmdbId。取不到父目录时静默跳过。
    /// </remarks>
    private void InvalidateFolderCache(string sourcePath)
    {
        string? folderKey;
        try { folderKey = Path.GetDirectoryName(sourcePath); }
        catch { return; }
        if (!string.IsNullOrEmpty(folderKey)) _folderCache.Remove(folderKey);
    }

    /// <summary>把人工确认 / 改绑的 TV 绑定沉淀进同目录 series 复用缓存（人工裁决置信度按 1.0 记）</summary>
    /// <remarks>
    /// 键构造与 InvalidateFolderCache / ProcessFileService.TryFolderKey 同构（文件父目录完整路径）。
    /// 读取侧有双重守门：平铺监控根目录（folderReuseEligible=false）根本不会查此缓存；
    /// 专属子目录复用前还要过 CanReuseFolderSeries 标题相似度——沉淀条目最坏被守门拒绝，绝不张冠李戴。
    /// 仅 TV 沉淀（电影同目录常混放多部，锁定会误染后续文件），与 ProcessFileService 采纳段写入口径一致。
    /// </remarks>
    private void SeedFolderCache(string sourcePath, int tmdbId, string mediaType, string? title, int? year)
    {
        string? folderKey;
        try { folderKey = Path.GetDirectoryName(sourcePath); }
        catch { return; }
        if (string.IsNullOrEmpty(folderKey)) return;
        _folderCache.Set(folderKey, new FolderSeriesEntry(
            TmdbId: tmdbId,
            MediaType: mediaType,
            Title: title,
            Year: year,
            Confidence: 1.0));
        _logger.LogDebug("人工绑定沉淀同目录 series 缓存：folder={Folder}, tmdbId={TmdbId}", folderKey, tmdbId);
    }

    private static string? ToPosterUrl(string? posterPath)
    {
        if (string.IsNullOrEmpty(posterPath)) return null;
        return $"{TmdbImageBase}{(posterPath.StartsWith('/') ? posterPath : "/" + posterPath)}";
    }

}
