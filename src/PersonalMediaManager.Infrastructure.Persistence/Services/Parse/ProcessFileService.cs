using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.ParseTasks;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Review;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>主处理编排（D7.1）— 单文件全链路状态机驱动</summary>
/// <remarks>
/// 流转（与需求文档 §6.2 + MediaItem 状态机表对齐）：
///
///   Detected → Queued → Parsing
///     │
///     ├── 规则置信度 ≥ 阈值 且无特殊字符 → TmdbMatching → 查 TMDB
///     │                                       ├── 候选 ∈ [1, N] → Classifying
///     │                                       └── 候选 = 0 或 &gt; N → AiParsing
///     └── 规则置信度 &lt; 阈值 或 有特殊字符 → AiParsing
///
///   AiParsing → 调 AI（IAiCallOrchestrator 内部硬上限 2 次）
///     │
///     ├── AI Success → TmdbRematching → 查 TMDB
///     │                    ├── 候选 ∈ [1, N] → Classifying
///     │                    └── 其他 → AwaitingReview
///     └── AI Fail → TmdbRematching → AwaitingReview （状态机要求 AiParsing 必走 TmdbRematching）
///
///   Classifying → IClassifyService.ClassifyAsync
///     ├── Matched → Archiving
///     └── SendToReview → AwaitingReview
///
///   Archiving → IArchiveService.ArchiveAsync
///     ├── Completed → Completed
///     └── ConflictSkipped → Skipped
///
/// 异常路径（IO / DB / TMDB 异常 / TmdbClientException）：catch → MediaItem.MarkFailed → 返回 Failed。
///
/// 幂等：按 SourcePath 查 MediaItem。
///   - 终态（Completed / Skipped / Ignored / Cancelled / Failed）→ 直接返回 Skipped（Failed 需由 History.Rescan 重置回 Queued）
///   - 进行中（Parsing / TmdbMatching / ...）→ 视为孤立状态可继续往下走
///     （理论上 TaskProcessor 单消费 + Semaphore(1,1) 不会出现并发进行中；
///     这种情况说明上次 worker crash 在中间，本次重新走一遍）
/// </remarks>
internal sealed class ProcessFileService : IProcessFileService
{
    private const double DefaultConfidenceThreshold = 0.6;
    /// <summary>候选阈值 N 的兜底默认值：仅当 Tmdb_Setting 单例行缺失时使用（正常运行时读库取用户配置）</summary>
    private const int DefaultCandidateThreshold = 3;
    private const int WriteCompletionStableSeconds = 5;
    private const int WriteCompletionTimeoutSeconds = 300;
    /// <summary>多候选择优门槛：候选 ≥ 2 时最高综合得分低于此值 → 视为无法可信取舍，转人工审核</summary>
    private const double MultiCandidateMinScore = 0.5;
    /// <summary>单候选下限（比多候选门槛更宽松）：防残缺标题模糊命中唯一一条错误结果被直接采纳</summary>
    private const double SingleCandidateMinScore = 0.35;
    /// <summary>文件夹级 series 复用的标题相似度阈值：本文件规则标题与缓存剧名归一化相似度 ≥ 此值才复用</summary>
    private const double FolderReuseTitleSimilarityThreshold = 0.6;
    /// <summary>AI 别名兜底重搜上限：中文 title 二次 TMDB 不中时，最多用前 N 个 AI 别名各重搜一次（带搜索缓存，额外开销可控）</summary>
    private const int MaxAliasRetry = 3;
    /// <summary>候选过多免 AI 裁决：四维打分榜首 ≥ 此值且领先次名 ≥ 间距门槛才视为「唯一可信」直接采纳</summary>
    private const double CrossCheckDominantScore = 0.7;
    /// <summary>候选过多免 AI 裁决：榜首与次名的最小得分间距（分差太小说明歧义仍在，交由交叉投票 / AI）</summary>
    private const double CrossCheckDominantGap = 0.15;
    /// <summary>「归档前拦截」开关键：开启后命中分类的高置信记录也先进 AwaitingReview 待人工确认（默认关，与 GeneralSettingsService.KnownSettings / ArchiveService 同名约定）</summary>
    private const string HoldBeforeArchiveKey = "Archive_HoldBeforeArchive";

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IWriteCompletionDetector _writeDetector;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IForcedMatchMarkerStore _forcedMatch;
    private readonly ITmdbSearchService _tmdb;
    private readonly IAiCallOrchestrator _aiOrchestrator;
    private readonly IFolderSeriesCache _folderCache;
    private readonly IClassifyService _classify;
    private readonly IArchiveService _archive;
    private readonly IFileHasher _fileHasher;
    private readonly IFileProbe _fileProbe;
    private readonly IMediaAudioProbe _audioProbe;
    private readonly ITaskNotifier _notifier;
    private readonly IWebhookEmitter _webhook;
    private readonly IClock _clock;
    private readonly ILogger<ProcessFileService> _logger;

    public ProcessFileService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IWriteCompletionDetector writeDetector,
        IRuleEngineService ruleEngine,
        IForcedMatchMarkerStore forcedMatch,
        ITmdbSearchService tmdb,
        IAiCallOrchestrator aiOrchestrator,
        IFolderSeriesCache folderCache,
        IClassifyService classify,
        IArchiveService archive,
        IFileHasher fileHasher,
        IFileProbe fileProbe,
        IMediaAudioProbe audioProbe,
        ITaskNotifier notifier,
        IWebhookEmitter webhook,
        IClock clock,
        ILogger<ProcessFileService> logger)
    {
        _dbFactory = dbFactory;
        _writeDetector = writeDetector;
        _ruleEngine = ruleEngine;
        _forcedMatch = forcedMatch;
        _tmdb = tmdb;
        _aiOrchestrator = aiOrchestrator;
        _folderCache = folderCache;
        _classify = classify;
        _archive = archive;
        _fileHasher = fileHasher;
        _fileProbe = fileProbe;
        _audioProbe = audioProbe;
        _notifier = notifier;
        _webhook = webhook;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken cancellationToken)
    {
        // 1. 写入完成判定（哨兵 .complete 或 5s 大小稳定窗口；源文件不存在时探测器短路返回 false）
        bool writeOk = await _writeDetector.WaitUntilCompleteAsync(
            item.FullPath, WriteCompletionStableSeconds, WriteCompletionTimeoutSeconds, cancellationToken);
        if (!writeOk)
        {
            // 应用关停取消导致的中断不是文件问题：不终态化，维持原状交给 StartupRecoveryWorker 下次启动重排
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("写入完成检测因取消中断，维持原状待启动恢复重排：{Path}", item.FullPath);
                return new ProcessFileOutcome(0, ProcessOutcome.Skipped);
            }
            // 旧实现此处直接 return Skipped（在加载 MediaItem 之前），FileIntakeService 先建的 Detected/Queued 行
            // 原样滞留成僵尸：大文件（>300s 写入）永久漏处理、被删文件每次重启被重排并空轮询、监控目录删不掉。
            // 改为加载 DB 行并终态化 Failed（可 Rescan / 强制全扫自动重投）：大文件写完后下一轮扫描自动救回，闭环成立。
            return await FinalizeWriteDetectionFailureAsync(item.FullPath, cancellationToken);
        }

        // 2. 加载或创建 MediaItem（按 SourcePath 幂等）
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        MediaItem? existing = await db.MediaItems
            .FirstOrDefaultAsync(m => m.SourcePath == item.FullPath, cancellationToken);

        if (existing is not null && existing.IsTerminal())
        {
            _logger.LogInformation("同路径 MediaItem 已是终态({Status})，跳过：{Path}", existing.Status, item.FullPath);
            return new ProcessFileOutcome(existing.Id, ProcessOutcome.Skipped);
        }

        MediaItem media = existing ?? CreateMediaItem(item.FullPath);
        if (existing is null)
        {
            db.MediaItems.Add(media);
            await db.SaveChangesAsync(cancellationToken);
        }

        // 2.5 内容去重：算内容采样哈希（仅在尚未计算时），命中已成功归档（Completed）的同内容记录 →
        //     直接 Detected→Skipped 终态，跳过整条解析/归档管线、不做任何文件操作（避免重复入库）。
        //     去重是增益特性：哈希失败（TryComputeAsync 返回 null）→ 跳过去重照常处理，绝不阻断主流程；
        //     仅在初始 Detected 时短路 Skipped（续跑的非 Detected 记录已越过本关，补记哈希后继续，避免非法转移）。
        if (media.FileHash is null)
        {
            string? hash = await _fileHasher.TryComputeAsync(item.FullPath, cancellationToken);
            if (hash is not null)
            {
                media.SetFileHash(hash);
                if (media.Status == MediaItemStatus.Detected)
                {
                    // 防丢核心：仅当存在「同内容 + 已成功归档 + 归档副本确实还在盘上」的记录时才判重复跳过。
                    // 若历史归档副本已被外部删除/移走（TargetPath 指向的文件不存在），绝不把新文件当重复——
                    // 否则旧副本已没、新文件又被跳过不归档 = 内容彻底丢失。多条同 hash 取第一条副本仍在的。
                    List<DuplicateCandidate> sameHash = await db.MediaItems
                        .Where(m => m.FileHash == hash
                                 && m.Status == MediaItemStatus.Completed
                                 && m.Id != media.Id)
                        .Select(m => new DuplicateCandidate(m.Id, m.TargetPath))
                        .ToListAsync(cancellationToken);

                    DuplicateCandidate? liveDup = sameHash.FirstOrDefault(d =>
                        !string.IsNullOrWhiteSpace(d.TargetPath) && _fileProbe.FileExists(d.TargetPath));

                    if (liveDup is not null)
                    {
                        long duplicateOf = liveDup.Id;
                        media.AppendStep(MediaItemStatus.Detected, _clock.UtcNow, durMs: 0,
                            JsonSerializer.Serialize(new { fileHash = hash, duplicateOf, decision = "内容与已归档记录重复 → 跳过（未做任何文件操作）" }, StepJsonOptions));
                        media.Transition(MediaItemStatus.Skipped);
                        media.AppendStep(MediaItemStatus.Skipped, _clock.UtcNow, durMs: 0,
                            JsonSerializer.Serialize(new { reason = "内容去重命中", duplicateOf }, StepJsonOptions));
                        await db.SaveChangesAsync(cancellationToken);
                        await NotifyAsync(media, MediaItemStatus.Detected, cancellationToken);
                        await EmitSkippedAsync(media, "内容去重命中", cancellationToken);
                        _logger.LogInformation("内容去重命中 → Skipped：{Path}（重复于 MediaItemId={DupId}）", item.FullPath, duplicateOf);
                        return new ProcessFileOutcome(media.Id, ProcessOutcome.Skipped);
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        // 解析上下文：从 PendingFileItem.WatchFolderId 查 WatchFolder.Path，算「监控根→文件」的 RelativeSegments；
        // WatchFolderId=0（手动扫描 / 测试场景）或目录已删除 → 退化为单层父目录上下文（FileParseContext.FromFullPath null watchRoot）
        string? watchRoot = null;
        if (item.WatchFolderId > 0)
        {
            watchRoot = await db.WatchFolders
                .Where(w => w.Id == item.WatchFolderId)
                .Select(w => w.Path)
                .FirstOrDefaultAsync(cancellationToken);
        }
        FileParseContext parseContext = FileParseContext.FromFullPath(item.FullPath, watchRoot);

        try
        {
            return await RunPipelineAsync(db, media, parseContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessFile 失败：{Path}", item.FullPath);
            await SafeMarkFailedAsync(db, media, ex.Message, cancellationToken);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.Failed);
        }
    }

    /// <summary>内容去重候选投影：同 hash 的已归档记录 Id + 其归档落点（用于校验副本是否仍在盘上）</summary>
    private sealed record DuplicateCandidate(long Id, string? TargetPath);

    private async Task<ProcessFileOutcome> RunPipelineAsync(
        PmmDbContext db,
        MediaItem media,
        FileParseContext parseContext,
        CancellationToken ct)
    {
        // TMDB 决策参数：候选阈值 N 与四维打分权重运行时读 Tmdb_Setting（用户在设置页可调），
        // 单例行缺失时回退种子默认值（N=3，权重 0.5/0.3/0.1/0.1）——此前两者均为死旋钮（硬编码 / 零消费点）。
        TmdbSetting? tmdbSetting = await db.TmdbSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        int candidateThreshold = tmdbSetting?.CandidateThreshold ?? DefaultCandidateThreshold;
        TmdbScoreWeights scoreWeights = tmdbSetting is null
            ? TmdbScoreWeights.Default
            : new TmdbScoreWeights(
                tmdbSetting.ScoreWeightTitle, tmdbSetting.ScoreWeightYear,
                tmdbSetting.ScoreWeightPopularity, tmdbSetting.ScoreWeightLanguage);
        string preferredLanguage = tmdbSetting?.Language ?? "zh-CN";

        // 文件夹级 series 复用：仅当文件位于「监控根的子目录」（专属剧集文件夹，如 …\某剧 S01\）时才启用。
        // 直接堆在监控根下的文件（RelativeSegments 为空）属于混合下载区——不同电影、不同剧的单集会平铺堆叠，
        // 按目录复用会张冠李戴（典型事故：下载根目录里第一部电影锁定后，后续无关文件全被套成同一 TMDB）；
        // 监控根未知（手动扫描 / WatchFolderId=0 / 文件不在任何监控根下）同样保守地不启用复用。
        // reusedFromCache 标记本次是否走复用路径（决定 ParseSource 与是否回写缓存）。
        bool folderReuseEligible = parseContext.WatchRoot is not null && parseContext.RelativeSegments.Count > 0;
        string? folderKey = folderReuseEligible ? TryFolderKey(media.SourcePath) : null;
        bool reusedFromCache = false;
        // 强制匹配标识（pmm.txt / TMDB URL）命中标记：决定 ParseSource=Manual、跳过规则/AI/搜索与择优打分
        bool forcedMatch = false;
        // 剧集组翻译会把「编组内集号」改写成 TMDB 正典季集，但磁盘上的视频 / 字幕文件名仍是编组内原始集号。
        // 这里记下翻译前的「源文件名命名空间」季集，下游随 ParsedInfo 透传到归档阶段做字幕等伴生文件归属匹配
        // （命名仍用正典季集）。仅剧集组翻译路径会赋值，其余路径保持 null（归档侧回退正典，行为不变）。
        int? originalSeason = null, originalEpisode = null, originalEpisodeEnd = null;

        // 时间线追踪：enteredAt = 当前 stage 进入时刻；每次 Transition 时先 AppendStep 记上一 stage 的耗时+detail
        // 终态（Completed/Skipped/AwaitingReview/Failed）则直接 AppendStep 记自身（durMs=0）
        DateTimeOffset enteredAt = _clock.UtcNow;

        void RecordExit(MediaItemStatus exitingStage, object? detail)
        {
            DateTimeOffset now = _clock.UtcNow;
            long durMs = Math.Max(0L, (long)(now - enteredAt).TotalMilliseconds);
            string? json = detail is null ? null : JsonSerializer.Serialize(detail, StepJsonOptions);
            media.AppendStep(exitingStage, enteredAt, durMs, json);
            enteredAt = now;
        }

        void RecordTerminal(MediaItemStatus terminalStage, object? detail)
        {
            string? json = detail is null ? null : JsonSerializer.Serialize(detail, StepJsonOptions);
            media.AppendStep(terminalStage, _clock.UtcNow, durMs: 0, json);
        }

        // 状态进入 Parsing（如果 worker 上次崩在中间，按当前状态续跑）
        if (media.Status == MediaItemStatus.Detected)
        {
            RecordExit(MediaItemStatus.Detected, new { folder = TryParentFolderName(media.SourcePath), trigger = "FileSystemWatcher" });
            media.Transition(MediaItemStatus.Queued);
        }
        if (media.Status == MediaItemStatus.Queued)
        {
            RecordExit(MediaItemStatus.Queued, new { semaphore = "TaskProcessor 全局信号量获取" });
            media.Transition(MediaItemStatus.Parsing);
            await db.SaveChangesAsync(ct);
        }

        // 3. 规则引擎（基于 FileParseContext 多层路径段）
        string? parentFolderName = parseContext.DirectParentFolderName;
        RuleParseResult rule = await _ruleEngine.ParseAsync(parseContext, ct);

        // 本地优先剧集解析（持久化映射预热）：同一部剧的多个文件通常落在同一专属子目录，series 身份
        // （tmdbId / 类型 / 剧名 / 年份）是共享信息，仅季 / 集逐文件不同。这里把「同文件夹已成功归档兄弟集」
        // 的 series 身份从持久库（Media_Item 本身即持久存储，无需额外表）还原进进程内缓存，
        // 让规则直查路径、AI 兜底路径、以及进程重启后的首集都能复用 —— 跳过对同一部剧的重复 TMDB 搜索（与 AI 兜底）。
        // 仅在专属剧集子目录启用（folderReuseEligible 已排除监控根平铺堆放区）；L1 缓存已有则不重复查库。
        // 命中后仍由 CanReuseFolderSeries 标题相似度守门把关，混放多剧子目录最坏回落正常流程，绝不张冠李戴。
        if (folderReuseEligible && folderKey is not null && _folderCache.TryGet(folderKey) is null)
        {
            FolderSeriesEntry? persisted = await TryResolveSeriesFromDbAsync(db, folderKey, ct);
            if (persisted is not null)
            {
                _folderCache.Set(folderKey, persisted);
                _logger.LogDebug(
                    "本地剧集映射命中（同文件夹已归档兄弟集）→ 预热缓存以跳过 TMDB：folder={Folder}, tmdbId={TmdbId}",
                    folderKey, persisted.TmdbId);
            }
        }

        // 4. 决策：UseTmdb 还是 CallAi
        ParseTask task = ParseTask.AfterRuleEngine(
            rule.Confidence, rule.HasSpecialChars,
            DefaultConfidenceThreshold, candidateThreshold);
        NextAction firstDecision = task.DecideAfterFirstTmdb();

        TmdbSearchResult? tmdb = null;

        // 该次 TMDB 步骤的数据来源标签（前端时间线据此显示彩色来源标识）：
        //   manual = 命中强制匹配标识（pmm.txt / TMDB URL），直接锚定指定条目
        //   reuse  = 命中本地剧集映射（内存 L1 / 持久 L2 兄弟集），未发任何搜索
        //   cache  = 命中本地搜索缓存（Tmdb_SearchCache，不计远端额度）
        //   remote = 远端拉取 TMDB
        // 闭包读取 forcedMatch / reusedFromCache / tmdb 的调用时刻值（均在赋值后才调用）。
        string TmdbSourceLabel() => forcedMatch ? "manual" : reusedFromCache ? "reuse" : (tmdb?.FromCache == true ? "cache" : "remote");

        // === 强制匹配标识短路（pmm.txt / TMDB URL）===
        // 命中即锚定指定 TMDB 条目，跳过规则置信度判定、特殊字符转 AI、TMDB 搜索、AI 升级链全部分支；
        // 剧集组（episode_group）模式额外把"编组内集号"翻译成正典季集（HD Remaster 等重制版）。
        // 放在规则解析后：季/集是逐文件信息，由规则解析提供；标识只锚定 series 身份与可选季/剧集组。
        ForcedMatchMarker? forced = await _forcedMatch.TryReadAsync(parseContext, ct);
        if (forced is not null)
        {
            forcedMatch = true;
            // 拉详情并定类型：pmm.txt / TMDB URL 标识自带类型（forced.MediaType 非空）直接拉；
            // 文件夹名 {tmdb-NNN} 标识只给 id（forced.MediaType 为 null），先用规则识别的类型拉、
            // 拉取失败（多半 tv/movie 猜反）再翻另一类型兜底（详见 FetchForcedDetailsAsync）。
            (TmdbDetailsResult Details, string ResolvedType)? fetched =
                await FetchForcedDetailsAsync(forced, rule.MediaType, media.SourcePath, ct);
            if (fetched is null)
            {
                // 标识里的 TMDB id 无效 / 拉取失败（文件夹名标识时 tv+movie 两种类型均已尝试）：转人工审核
                RecordExit(MediaItemStatus.Parsing, new
                {
                    forced = new { forced.TmdbId, type = forced.MediaType ?? "(规则识别)" },
                    source = "manual",
                    decision = $"强制匹配标识 TMDB id={forced.TmdbId} 详情拉取失败 → AwaitingReview",
                });
                media.Transition(MediaItemStatus.TmdbMatching);
                MediaItemStatus beforeReview = media.Status;
                media.MarkAwaitingReview(ReviewReason.TmdbZeroResult);
                RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "强制匹配标识的 TMDB id 无效或拉取失败" });
                await db.SaveChangesAsync(ct);
                await NotifyAsync(media, beforeReview, ct);
                await EmitReviewCreatedAsync(media, ct);
                return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
            }
            TmdbDetailsResult details = fetched.Value.Details;
            string resolvedType = fetched.Value.ResolvedType;

            // 季 / 集：剧集组模式翻译"编组内集号 → 正典季集"；否则季用覆盖值（缺省回退规则季）、集与末集用规则解析值
            int? forcedSeason = forced.Season ?? rule.Season;
            int? forcedEpisode = rule.Episode;
            int? forcedEpisodeEnd = rule.EpisodeEnd;
            string? translateNote = null;
            bool isTvForced = string.Equals(resolvedType, "tv", StringComparison.OrdinalIgnoreCase);
            if (isTvForced && forced.EpisodeGroupId is not null && rule.Episode is int fileEpNo)
            {
                // 一次拉取剧集组分组（按 Order 升序），起始集与双集末集复用同一份映射本地翻译，避免重复拉取
                List<TmdbEpisodeGroupEntry>? segment = await TryResolveEpisodeGroupSegmentAsync(forced, ct);
                (int Season, int Episode)? Translate(int groupPos) =>
                    segment is not null && groupPos >= 1 && groupPos <= segment.Count
                        ? (segment[groupPos - 1].SeasonNumber, segment[groupPos - 1].EpisodeNumber)
                        : null;

                if (Translate(fileEpNo) is { } mp)
                {
                    // 翻译成功：先留存「源文件名命名空间」的原始季集（rule.* 此刻尚未被正典值覆盖，见下方 L426），
                    // 供归档阶段字幕等伴生文件按磁盘原始集号归属匹配；正典季集随后覆盖 forced* 用于命名。
                    // 原始末集恒取 rule.EpisodeEnd：源文件即便是双集，正典翻译退化为单集也只影响命名，不改源命名空间。
                    originalSeason = rule.Season;
                    originalEpisode = fileEpNo;
                    originalEpisodeEnd = rule.EpisodeEnd;
                    forcedSeason = mp.Season;
                    forcedEpisode = mp.Episode;
                    // 双集 / 多集合并末集一并翻译：仅当与起始集同季、且编组该段顺序映射（正典跨度 == 编组内跨度）
                    // 才保留为合法连续区间；编组重排导致乱序 / 跨季 / 末集越界时退化为单集，绝不产生 S01E02-E01 之类非法区间。
                    if (rule.EpisodeEnd is int fileEpEnd && fileEpEnd > fileEpNo)
                    {
                        int span = fileEpEnd - fileEpNo;
                        if (Translate(fileEpEnd) is { } me && me.Season == mp.Season && me.Episode == mp.Episode + span)
                        {
                            forcedEpisodeEnd = me.Episode;
                            translateNote = $"剧集组翻译：第 {fileEpNo}-{fileEpEnd} 集 → S{mp.Season:D2}E{mp.Episode:D2}-E{me.Episode:D2}";
                        }
                        else
                        {
                            forcedEpisodeEnd = null; // 末集翻译后乱序 / 跨季 / 越界 → 退化单集
                            translateNote = $"剧集组翻译：第 {fileEpNo} 集 → S{mp.Season:D2}E{mp.Episode:D2}（双集末集 {fileEpEnd} 翻译后非连续，退化为单集）";
                            _logger.LogInformation("强制匹配剧集组双集末集翻译后非连续，退化单集：tmdbId={TmdbId}, eg={Eg}, 文件集号={Start}-{End}",
                                forced.TmdbId, forced.EpisodeGroupId, fileEpNo, fileEpEnd);
                        }
                    }
                    else
                    {
                        // 单集：起始集翻译后正典集号已变，原始末集失去意义（本就应为 null，显式清空兜底异常数据）
                        forcedEpisodeEnd = null;
                        translateNote = $"剧集组翻译：第 {fileEpNo} 集 → S{mp.Season:D2}E{mp.Episode:D2}";
                    }
                }
                else
                {
                    // 翻译失败（集号越界 / 多分组未指定 group / 拉取失败）：清空集号与末集，交由下方剧集完整性守护转人工
                    forcedEpisode = null;
                    forcedEpisodeEnd = null;
                    translateNote = $"剧集组翻译失败（第 {fileEpNo} 集无法映射）→ 待人工补季集";
                    _logger.LogWarning("强制匹配剧集组翻译失败：tmdbId={TmdbId}, eg={Eg}, group={Group}, 文件集号={Ep}",
                        forced.TmdbId, forced.EpisodeGroupId, forced.GroupId ?? "-", fileEpNo);
                }
            }

            // 用锚定结果覆盖规则字段：下游"采用候选"段从 rule.* 构造 ParsedInfo（aiResult 为 null）
            rule = rule with
            {
                Title = details.Title ?? forced.TitleOverride ?? rule.Title,
                Year = details.Year ?? rule.Year,
                MediaType = resolvedType,
                Season = forcedSeason,
                Episode = forcedEpisode,
                EpisodeEnd = forcedEpisodeEnd,
                Confidence = 1.0,
            };
            tmdb = SynthesizeForcedCandidate(forced.TmdbId, resolvedType, details);
            firstDecision = NextAction.UseTmdb;
            RecordExit(MediaItemStatus.Parsing, new
            {
                forced = new { forced.TmdbId, type = resolvedType, typeSource = forced.MediaType is null ? "rule" : "marker", forced.Season, forced.EpisodeGroupId, forced.GroupId },
                source = "manual",
                extracted = new { title = rule.Title, year = rule.Year, season = rule.Season, episode = rule.Episode, episodeEnd = rule.EpisodeEnd },
                translate = translateNote,
                decision = forced.MediaType is null
                    ? "命中文件夹名 {tmdb-NNN} 标记 → 锚定 TMDB id（类型/季/集按规则识别），跳过 AI/TMDB 搜索"
                    : "命中强制匹配标识（pmm.txt / TMDB URL）→ 跳过规则/AI/TMDB 搜索，直接锚定指定条目",
            });
            media.Transition(MediaItemStatus.TmdbMatching);
            await db.SaveChangesAsync(ct);
        }

        if (!forcedMatch && firstDecision == NextAction.UseTmdb)
        {
            // 规则直查路径优先复用本地剧集映射：命中（TV + 标题相似）则跳过 TMDB 搜索，直接用已知 tmdbId，
            // 季 / 集仍取本文件规则结果（剧集字段不全由下游守护处理）。firstDecision 保持 UseTmdb，
            // 合成单候选直接进入「采用候选」段，不再触发 AI 兜底分支。
            FolderSeriesEntry? cachedSeries = folderKey is null ? null : _folderCache.TryGet(folderKey);
            if (cachedSeries is not null && CanReuseFolderSeries(cachedSeries, rule))
            {
                reusedFromCache = true;
                tmdb = SynthesizeSeriesCandidate(cachedSeries);
                RecordExit(MediaItemStatus.Parsing, new
                {
                    cleaned = rule.Title,
                    confidence = rule.Confidence,
                    extracted = new { title = rule.Title, year = rule.Year, type = rule.MediaType, season = rule.Season, episode = rule.Episode, episodeEnd = rule.EpisodeEnd },
                    reusedFromFolderCache = true,
                    tmdbId = cachedSeries.TmdbId,
                    decision = "命中同文件夹已归档剧集映射 → 跳过 TMDB 搜索（季 / 集取本文件规则结果）",
                    matchedRuleId = rule.MatchedRuleId,
                });
                media.Transition(MediaItemStatus.TmdbMatching);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                RecordExit(MediaItemStatus.Parsing, new
                {
                    cleaned = rule.Title,
                    confidence = rule.Confidence,
                    extracted = new { title = rule.Title, year = rule.Year, type = rule.MediaType, season = rule.Season, episode = rule.Episode, episodeEnd = rule.EpisodeEnd },
                    decision = $"规则置信度 {rule.Confidence:F2} ≥ 阈值 {DefaultConfidenceThreshold:F2} → 走 TMDB 直查",
                    matchedRuleId = rule.MatchedRuleId,
                });
                media.Transition(MediaItemStatus.TmdbMatching);
                await db.SaveChangesAsync(ct);
                tmdb = await _tmdb.SearchAsync(new TmdbSearchRequest(rule.Title, rule.MediaType, rule.Year), ct);

                ParseTask afterFirstTmdb = ParseTask.AfterTmdbQuery(
                    rule.Confidence, tmdb.Candidates.Count, rule.HasSpecialChars,
                    DefaultConfidenceThreshold, candidateThreshold);
                firstDecision = afterFirstTmdb.DecideAfterFirstTmdb();
            }
        }

        AiParseResult? aiResult = null;
        // 别名兜底命中时记录命中的别名（供成功时间线展示「靠哪个别名搜到」）；null = 未走别名兜底或未命中。
        // aliasFromLocal 区分别名来源：true = 规则引擎本地备选标题（未动用 AI），false = AI 返回的检索别名
        string? matchedSearchAlias = null;
        bool aliasFromLocal = false;
        // 本地备选标题已尝试个数（全部未命中时写进 AI 兜底时间线，说明「已先试过本地重搜」）
        int localAliasesTried = 0;
        // 免 AI 裁决说明（打分显著 / 交叉投票命中时赋值）：覆盖下游采纳段时间线的默认 decision 文案，
        // 否则「候选 {count} ≤ N」会与实际保留的多候选自相矛盾
        string? crossCheckNote = null;

        // === 免 AI 拦截 ①：首查候选过多 → 四维打分显著裁决 ===
        // 候选 >N 走 AI 的本意是「换更准的检索词重搜」，但热门标题首查 10+ 候选时年份 + 标题相似度
        // 往往已能唯一锁定。先用与采纳段同源的四维打分（标题/年份/热度/语言）排一次：榜首得分够高
        // 且与次名拉开差距即直接采纳；分差含糊再进入 ② 交叉投票 / AI，误差方向永远是「多查少猜」。
        if (firstDecision == NextAction.CallAi && tmdb is not null && tmdb.Candidates.Count > candidateThreshold)
        {
            IReadOnlyList<TmdbCandidateScore> preRanked = TmdbCandidateScorer.Rank(
                tmdb.Candidates, [rule.Title], rule.Year, scoreWeights, preferredLanguage);
            // 用户可把候选阈值 N 配到 1 以下（候选 2 个即「过多」），榜首即全部时视为无次名、间距充分
            double gap = preRanked.Count > 1 ? preRanked[0].Score - preRanked[1].Score : double.MaxValue;
            if (preRanked[0].Score >= CrossCheckDominantScore && gap >= CrossCheckDominantGap)
            {
                tmdb = tmdb with { Candidates = preRanked.Select(r => r.Candidate).ToList() };
                firstDecision = NextAction.UseTmdb;
                crossCheckNote = $"候选 {tmdb.Candidates.Count} 个 > N={candidateThreshold}，但四维打分榜首 " +
                    $"{preRanked[0].Score:F2} ≥ {CrossCheckDominantScore:F2} 且领先次名 {gap:F2} ≥ {CrossCheckDominantGap:F2} → 唯一可信，免 AI 直接采纳";
                _logger.LogInformation(
                    "候选过多但四维打分显著 → 免 AI 采纳：{Path}（榜首 {Top:F2}，领先 {Gap:F2}）",
                    media.SourcePath, preRanked[0].Score, gap);
            }
        }

        // 全程零候选跟踪（免 AI 拦截 ③ 判定用）：主标题查询与全部备选查询均零结果 = TMDB 大概率未收录
        bool anyCandidateSeen = tmdb is { Candidates.Count: > 0 };

        // === 免 AI 拦截 ②：本地备选标题重搜 + 交叉投票 ===
        // CallAi 的三类成因（混排 / 低置信 / 首查候选不符）中相当一部分只是「主标题选错了语言或层级」：
        // 英文主标题在 TMDB 首选语言 zh-CN 下搜不到但路径目录里带中文剧名、混排标题整串搜索失配等。
        // 在真正烧 AI 之前，用规则引擎同时产出的本地备选标题（主标题拆分段 + 其余路径层标题）逐个重搜：
        //   · 备选搜得可采纳候选（[1,N]）→ 直接转回 UseTmdb（换词直中，最强证据）；
        //   · 备选也搜得多候选 → 与首查候选交叉计票——多个不同检索词都命中同一条目是强消歧信号，
        //     循环后唯一最高票且四维得分达标者免 AI 采纳（多次 TMDB 查询对比替代 AI 裁决）。
        // 全程未动用 AI，ParseSource 保持 Rule；全部不中才进 AI 兜底。强制匹配已在上游短路。
        if (firstDecision == NextAction.CallAi && rule.AlternativeTitles is { Count: > 0 } localAliases)
        {
            // 混排 / 低置信从 Parsing 直接来：先补记 Parsing 退出并进入 TmdbMatching（重搜属 TMDB 匹配阶段语义）
            if (media.Status == MediaItemStatus.Parsing)
            {
                RecordExit(MediaItemStatus.Parsing, new
                {
                    cleaned = rule.Title,
                    confidence = rule.Confidence,
                    extracted = new { title = rule.Title, year = rule.Year, type = rule.MediaType, season = rule.Season, episode = rule.Episode, episodeEnd = rule.EpisodeEnd },
                    decision = rule.HasSpecialChars
                        ? $"标题中英/中日韩混排（特殊字符，规则置信度 {rule.Confidence:F2}）→ 先试本地备选标题重搜"
                        : $"规则置信度 {rule.Confidence:F2} < 阈值 {DefaultConfidenceThreshold:F2} → 先试本地备选标题重搜",
                });
                media.Transition(MediaItemStatus.TmdbMatching);
                await db.SaveChangesAsync(ct);
            }

            // 交叉投票仅在「首查候选过多」场景启用（零结果 / 低置信 / 混排没有可投票的首查候选集）；
            // 键 = (tmdbId, mediaType)，值 = (得票数, 首个投它的备选词——命中后供时间线展示)
            bool voteEligible = tmdb is not null && tmdb.Candidates.Count > candidateThreshold;
            Dictionary<(int Id, string Type), (int Votes, string FirstAlias)>? votes = voteEligible ? new() : null;

            foreach (string alias in localAliases.Take(MaxAliasRetry))
            {
                localAliasesTried++;
                TmdbSearchResult aliasTmdb = await _tmdb.SearchAsync(
                    new TmdbSearchRequest(alias, rule.MediaType, rule.Year), ct);
                if (aliasTmdb.Candidates.Count > 0) anyCandidateSeen = true;
                if (ParseTask.DecideAfterAiRetmdb(aliasTmdb.Candidates.Count, candidateThreshold) == NextAction.UseTmdb)
                {
                    tmdb = aliasTmdb;
                    firstDecision = NextAction.UseTmdb;
                    matchedSearchAlias = alias;
                    aliasFromLocal = true;
                    _logger.LogInformation(
                        "本地备选标题命中 → 免 AI 采用：{Path}（备选「{Alias}」搜得可采纳候选）", media.SourcePath, alias);
                    break;
                }
                // 备选结果同样过多（>N）：不直接丢弃，与首查候选求交集计票（换词仍反复出现的条目更可信）
                if (votes is not null && aliasTmdb.Candidates.Count > 0)
                {
                    foreach (TmdbCandidate c in aliasTmdb.Candidates)
                    {
                        (int Id, string Type) key = (c.Id, c.MediaType);
                        if (!tmdb!.Candidates.Any(f => f.Id == key.Id && f.MediaType == key.Type)) continue;
                        votes[key] = votes.TryGetValue(key, out (int Votes, string FirstAlias) v)
                            ? (v.Votes + 1, v.FirstAlias)
                            : (1, alias);
                    }
                }
            }

            // 循环后交叉投票裁决：换词未直中时，唯一最高票 + 四维得分 ≥ 多候选门槛 → 免 AI 采纳该候选。
            // 并列最高票 = 歧义仍在，弃权交 AI；得分门槛防「热门系列全员上榜」时票数领先但相似度不足的误配。
            if (firstDecision == NextAction.CallAi && votes is { Count: > 0 })
            {
                int maxVotes = votes.Values.Max(v => v.Votes);
                List<KeyValuePair<(int Id, string Type), (int Votes, string FirstAlias)>> winners =
                    votes.Where(kv => kv.Value.Votes == maxVotes).ToList();
                if (winners.Count == 1)
                {
                    (int Id, string Type) winnerKey = winners[0].Key;
                    TmdbCandidate winner = tmdb!.Candidates.First(c => c.Id == winnerKey.Id && c.MediaType == winnerKey.Type);
                    double winnerScore = TmdbCandidateScorer.Rank(
                            [winner], [rule.Title, winners[0].Value.FirstAlias], rule.Year, scoreWeights, preferredLanguage)[0].Score;
                    if (winnerScore >= MultiCandidateMinScore)
                    {
                        tmdb = tmdb with { Candidates = [winner] };
                        firstDecision = NextAction.UseTmdb;
                        matchedSearchAlias = winners[0].Value.FirstAlias;
                        aliasFromLocal = true;
                        crossCheckNote = $"首查候选过多，备选标题交叉投票唯一最高票（{maxVotes} 个检索词命中 tmdbId={winner.Id}，" +
                            $"四维得分 {winnerScore:F2} ≥ {MultiCandidateMinScore:F2}）→ 多次 TMDB 查询对比消歧，免 AI 采纳";
                        _logger.LogInformation(
                            "备选标题交叉投票命中 → 免 AI 采纳：{Path}（tmdbId={TmdbId}，得票 {Votes}，得分 {Score:F2}）",
                            media.SourcePath, winner.Id, maxVotes, winnerScore);
                    }
                }
            }
        }

        // === 免 AI 拦截 ③：全程零候选 + 规则高置信 → 判定 TMDB 未收录，跳过 AI ===
        // 走到这里且全零，意味着主标题 + 全部本地备选（每个查询自带语言 × 年份 4 层透明回退）都一无所获。
        // 标题解析可信（置信度达标、无混排）时，AI 重新清洗标题再搜大概率仍是零——这是「条目尚未收录」
        // （新番 / 新剧发布初期）的典型形态，烧 AI 无增量。直接转人工队列，并留给 TmdbZeroResultRetryJob
        // 每日自动重投：TMDB 收录后无人值守自动归档。低置信 / 混排不适用（AI 的标题清洗 + 检索别名有真实增量）。
        if (firstDecision == NextAction.CallAi && !anyCandidateSeen
            && !rule.HasSpecialChars && rule.Confidence >= DefaultConfidenceThreshold)
        {
            RecordExit(MediaItemStatus.TmdbMatching, new
            {
                query = $"{rule.Title} + {rule.MediaType} + {rule.Year}",
                source = TmdbSourceLabel(),
                localAliasesTried,
                decision = $"主标题与 {localAliasesTried} 个本地备选查询全部零结果，规则置信度 {rule.Confidence:F2} ≥ 阈值（标题可信）" +
                    "→ 判定 TMDB 暂未收录，跳过 AI 转人工；将每日自动重试，TMDB 收录后自动归档",
            });
            MediaItemStatus oldZero = media.Status;
            media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
            media.MarkAwaitingReview(ReviewReason.TmdbZeroResult);
            RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "TMDB 暂未收录（多查询全零结果），等待每日自动重试或人工绑定" });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(media, oldZero, ct);
            await EmitReviewCreatedAsync(media, ct);
            _logger.LogInformation("多查询全零结果（规则高置信）→ 跳过 AI 转 AwaitingReview（待每日自动重试）：{Path}", media.SourcePath);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
        }

        if (firstDecision == NextAction.CallAi)
        {
            // === 免 AI 拦截 ④：文件名与所有路径段均无可识别剧名 + TMDB 全零候选 → AI 也无从下手，跳过 AI 直接转人工 ===
            // 双保险把误判压到最低：HasNoTitleClue(文件名+路径剥离 发布组/季集/画质/hash 后全空) 且 !anyCandidateSeen(主标题+全部备选 TMDB 全零)。
            // 典型：S01E06_4K_60fps.mkv（纯季集+画质、无剧名、无父目录）——换 prompt 也补不出剧名，烧 AI 无增量，直接进人工队列。
            // 有父目录/路径剧名（如"南部档案"）或拉丁文本（拼音缩写）时 HasNoTitleClue=false，照常走 AI，绝不误拦。
            if (!anyCandidateSeen && MediaTitleClue.HasNoTitleClue(media.FileName, parseContext.RelativeSegments))
            {
                if (media.Status == MediaItemStatus.Parsing)
                {
                    RecordExit(MediaItemStatus.Parsing, new
                    {
                        cleaned = rule.Title,
                        confidence = rule.Confidence,
                        decision = "文件名与路径均无可识别剧名（剥离季集/画质/发布组后全空）+ TMDB 多查询全零 → 跳过 AI 直接转人工",
                    });
                    media.Transition(MediaItemStatus.TmdbMatching);
                }
                else
                {
                    RecordExit(MediaItemStatus.TmdbMatching, new
                    {
                        source = TmdbSourceLabel(),
                        localAliasesTried,
                        decision = "文件名与路径均无可识别剧名 + TMDB 多查询全零 → 跳过 AI 直接转人工",
                    });
                }
                MediaItemStatus oldNoClue = media.Status;
                media.MarkAwaitingReview(ReviewReason.ParseIncomplete);
                RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "文件名与路径缺乏可识别剧名，无法自动解析，待人工绑定" });
                await db.SaveChangesAsync(ct);
                await NotifyAsync(media, oldNoClue, ct);
                await EmitReviewCreatedAsync(media, ct);
                _logger.LogInformation("无剧名线索（文件名+路径剥离后全空）+ TMDB 全零 → 跳过 AI 转人工：{Path}", media.SourcePath);
                return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
            }

            // 进 AiParsing 之前可能从 Parsing 直接来（特殊字符 或 rule.Confidence 不达阈值），也可能从 TmdbMatching 来（候选>N）
            if (media.Status == MediaItemStatus.Parsing)
            {
                RecordExit(MediaItemStatus.Parsing, new
                {
                    cleaned = rule.Title,
                    confidence = rule.Confidence,
                    extracted = new { title = rule.Title, year = rule.Year, type = rule.MediaType, season = rule.Season, episode = rule.Episode, episodeEnd = rule.EpisodeEnd },
                    // 从 Parsing 进 AI 有两个互斥成因，须按真实成因分流文案：特殊字符（中英/中日韩混排）在决策矩阵中
                    // 优先级高于置信度判定（见 ParseTask.DecideAfterFirstTmdb），否则混排标题即便置信度 ≥ 阈值，
                    // 也会被硬编码成「< 阈值」的自相矛盾文案（如 0.95 < 0.60）误导用户。
                    decision = rule.HasSpecialChars
                        ? $"标题中英/中日韩混排（特殊字符），混排规则优先于置信度 → 触发 AI 兜底（规则置信度 {rule.Confidence:F2}）"
                        : $"规则置信度 {rule.Confidence:F2} < 阈值 {DefaultConfidenceThreshold:F2} → 触发 AI 兜底",
                });
            }
            else if (media.Status == MediaItemStatus.TmdbMatching)
            {
                // 三种到达路径的文案分流：主标题搜过（tmdb 非 null）/ 混排低置信直达（tmdb 为 null，仅重搜过备选）
                string tmdbDecision = localAliasesTried > 0
                    ? (tmdb is null
                        ? $"本地备选标题 {localAliasesTried} 个均未命中 → 触发 AI 兜底"
                        : $"候选 {tmdb.Candidates.Count} 个（>N={candidateThreshold} 或 =0），本地备选标题 {localAliasesTried} 个亦未命中 → 触发 AI 兜底")
                    : $"候选 {tmdb?.Candidates.Count ?? 0} 个（>N={candidateThreshold} 或 =0）→ 触发 AI 兜底";
                RecordExit(MediaItemStatus.TmdbMatching, new
                {
                    query = $"{rule.Title} + {rule.MediaType} + {rule.Year}",
                    source = TmdbSourceLabel(),
                    candidates = ProjectCandidates(tmdb),
                    localAliasesTried,
                    decision = tmdbDecision,
                });
            }
            media.Transition(MediaItemStatus.AiParsing);
            await db.SaveChangesAsync(ct);

            // 文件夹级复用：同目录已锁定 series（剧名 + 类型 + TMDBid）→ 跳过 AI 调用与二次 TMDB，
            // 季 / 集号仍取自本文件规则引擎结果（构造的 aiResult 季集留空 → 下游 ParsedInfo 回退到 rule）
            FolderSeriesEntry? cachedSeries = folderKey is null ? null : _folderCache.TryGet(folderKey);
            // 复用守门：仅 TV（电影各自独立、不共享 series）+ 本文件规则标题与缓存剧名足够相似——
            // 防同一子目录混放不同剧时张冠李戴；不满足则照常走 AI/TMDB（误差方向偏向多调一次 AI，绝不污染结果）。
            if (cachedSeries is not null && CanReuseFolderSeries(cachedSeries, rule))
            {
                reusedFromCache = true;
                tmdb = SynthesizeSeriesCandidate(cachedSeries);

                // 复用仅跳过 TMDB 搜索，不一概跳过 AI：season / episode 是逐文件信息，规则没解析出来时
                // （标准 Season NN 目录布局、纯集号文件名等）仍要调 AI 补齐——旧实现固定合成
                // Season:null / Episode:null 的 aiResult 把规则缺口原样传到下游，多季剧第二个文件起
                // 全部转人工审核（命中复用反而比不复用更差）。规则季集齐全才直通跳过 AI。
                if (rule.Season is not null && rule.Episode is not null)
                {
                    aiResult = new AiParseResult(
                        Title: cachedSeries.Title ?? rule.Title,
                        Year: cachedSeries.Year,
                        MediaType: cachedSeries.MediaType,
                        Season: null,
                        Episode: null,
                        EpisodeEnd: null,
                        Confidence: cachedSeries.Confidence ?? rule.Confidence);
                    RecordExit(MediaItemStatus.AiParsing, new
                    {
                        reusedFromFolderCache = true,
                        tmdbId = cachedSeries.TmdbId,
                        title = aiResult.Title,
                        decision = "复用同目录已锁定 series（规则季 / 集齐全），跳过 AI 兜底与二次 TMDB",
                    });
                }
                else
                {
                    media.MarkAiInvolved(); // AI 参与度统计：复用路径仍需 AI 补季 / 集，记为 AI 参与
                    AiCallOutcome ai = await _aiOrchestrator.ExecuteAsync(
                        new AiParseRequest(
                            FileName: media.FileName,
                            ParentFolderName: parentFolderName,
                            RuleHintTitle: rule.Title,
                            RuleHintYear: rule.Year,
                            RelativeSegments: parseContext.RelativeSegments,
                            RuleHintType: rule.MediaType,
                            RuleHintSeason: rule.Season,
                            RuleHintEpisode: rule.Episode,
                            RuleHintEpisodeEnd: rule.EpisodeEnd),
                        media.Id, ct);

                    // series 身份（tmdbId / 类型 / 剧名 / 年份）以复用绑定为准，AI 只用来补季 / 集；
                    // AI 失败也不转人工——TMDB 绑定仍在，季 / 集留空交由下游守护（单季自动补季 / ParseIncomplete）。
                    aiResult = new AiParseResult(
                        Title: cachedSeries.Title ?? ai.Result?.Title ?? rule.Title,
                        Year: cachedSeries.Year ?? ai.Result?.Year,
                        MediaType: cachedSeries.MediaType,
                        Season: ai.Result?.Season,
                        Episode: ai.Result?.Episode,
                        EpisodeEnd: ai.Result?.EpisodeEnd,
                        Confidence: ai.Result?.Confidence ?? cachedSeries.Confidence ?? rule.Confidence);
                    RecordExit(MediaItemStatus.AiParsing, new
                    {
                        reusedFromFolderCache = true,
                        tmdbId = cachedSeries.TmdbId,
                        title = aiResult.Title,
                        aiSuccess = ai.Success,
                        season = aiResult.Season,
                        episode = aiResult.Episode,
                        decision = ai.Success
                            ? "复用同目录已锁定 series（跳过二次 TMDB），规则缺季 / 集 → AI 兜底补齐"
                            : "复用同目录已锁定 series（跳过二次 TMDB），规则缺季 / 集且 AI 失败 → 交由下游季集守护",
                    });
                }
                media.Transition(MediaItemStatus.TmdbRematching);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                media.MarkAiInvolved(); // AI 参与度统计：发起升级链即记参与（失败转人工也保留过程事实）
                AiCallOutcome ai = await _aiOrchestrator.ExecuteAsync(
                    new AiParseRequest(
                        FileName: media.FileName,
                        ParentFolderName: parentFolderName,
                        RuleHintTitle: rule.Title,
                        RuleHintYear: rule.Year,
                        RelativeSegments: parseContext.RelativeSegments,
                        RuleHintType: rule.MediaType,
                        RuleHintSeason: rule.Season,
                        RuleHintEpisode: rule.Episode,
                        RuleHintEpisodeEnd: rule.EpisodeEnd),
                    media.Id, ct);

                RecordExit(MediaItemStatus.AiParsing, new
                {
                    success = ai.Success,
                    failureSummary = ai.FailureSummary,
                    output = ai.Result is null ? null : new { title = ai.Result.Title, year = ai.Result.Year, type = ai.Result.MediaType, confidence = ai.Result.Confidence },
                    // 逐级升级轨迹（喂给解析详情页 attempts 渲染）：试过哪几级、各级为何升级、最终哪级成功
                    attempts = (ai.Attempts ?? []).Select(a => new
                    {
                        level = a.Level,
                        provider = a.ProviderName,
                        providerType = a.ProviderType.ToString(),
                        isPrimary = a.IsPrimary,
                        success = a.Success,
                        confidence = a.Confidence,
                        errorType = a.ErrorType,
                        reason = a.ErrorDetail,
                        latencyMs = a.LatencyMs,
                        output = a.Success && ai.Result is not null
                            ? new { title = ai.Result.Title, year = ai.Result.Year, type = ai.Result.MediaType, confidence = ai.Result.Confidence }
                            : (object?)null,
                    }).ToArray(),
                    decision = ai.Success
                        ? $"AI 第 {(ai.Attempts is { Count: > 0 } att ? att[^1].Level : 1)} 级命中（置信度 {(ai.Result?.Confidence ?? 0):F2}）→ 二次查 TMDB"
                        : $"AI 升级链耗尽（{ai.ProvidersAttempted} 级）→ AwaitingReview",
                });
                // 状态机：AiParsing 必走 TmdbRematching（即使 AI 失败也要先转过去再到 AwaitingReview）
                media.Transition(MediaItemStatus.TmdbRematching);

                if (!ai.Success || ai.Result is null)
                {
                    media.RecordError(ai.FailureSummary ?? "AI 解析失败（无详细信息）");
                    RecordExit(MediaItemStatus.TmdbRematching, new { skipped = true, reason = "AI 失败，无可查询入参" });
                    MediaItemStatus oldAi = media.Status;
                    // AI 兜底失败 → AwaitingReview，原因 AiLowConfidence；候选全集（可能为空）落库供审核页展示
                    media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
                    media.MarkAwaitingReview(ReviewReason.AiLowConfidence);
                    RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = ai.FailureSummary ?? "AI 解析失败" });
                    await db.SaveChangesAsync(ct);
                    await NotifyAsync(media, oldAi, ct);
                    await EmitReviewCreatedAsync(media, ct);
                    _logger.LogInformation("AI 兜底失败 → AwaitingReview：{Path}", media.SourcePath);
                    return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
                }

                await db.SaveChangesAsync(ct);
                aiResult = ai.Result;
                tmdb = await _tmdb.SearchAsync(
                    new TmdbSearchRequest(aiResult.Title, aiResult.MediaType, aiResult.Year), ct);

                NextAction afterAi = ParseTask.DecideAfterAiRetmdb(tmdb.Candidates.Count, candidateThreshold);

                // 第 0 层（国漫/日漫元数据兜底）：中文 title 二次 TMDB 不中（零结果 / 多候选）→ 用 AI 给的检索别名
                // （原名 / 日文 / 英文官方译名 / 罗马音）逐个兜底重搜。TMDB 上冷门番剧 / 国产剧的主条目常是原名而非
                // 中文译名，zh-CN 搜索词命中不到 → 别名重搜把这批从人工队列捞回。任一别名得到 [1,N] 候选即采用并停止。
                if (afterAi == NextAction.SendToReview && aiResult.SearchAliases is { Count: > 0 } searchAliases)
                {
                    foreach (string alias in searchAliases.Take(MaxAliasRetry))
                    {
                        TmdbSearchResult aliasTmdb = await _tmdb.SearchAsync(
                            new TmdbSearchRequest(alias, aiResult.MediaType, aiResult.Year), ct);
                        if (ParseTask.DecideAfterAiRetmdb(aliasTmdb.Candidates.Count, candidateThreshold) == NextAction.UseTmdb)
                        {
                            tmdb = aliasTmdb;
                            afterAi = NextAction.UseTmdb;
                            matchedSearchAlias = alias;
                            _logger.LogInformation("AI 别名兜底命中 → 采用：{Path}（别名「{Alias}」搜得唯一候选）", media.SourcePath, alias);
                            break;
                        }
                    }
                }

                if (afterAi == NextAction.SendToReview)
                {
                    // 二次 TMDB 候选不符：候选数=0 归 TmdbZeroResult，>N 归 TmdbMultiCandidate
                    ReviewReason rematchReason = tmdb.Candidates.Count == 0
                        ? ReviewReason.TmdbZeroResult
                        : ReviewReason.TmdbMultiCandidate;
                    RecordExit(MediaItemStatus.TmdbRematching, new
                    {
                        query = $"{aiResult.Title} + {aiResult.MediaType} + {aiResult.Year}",
                        source = TmdbSourceLabel(),
                        candidates = ProjectCandidates(tmdb),
                        decision = $"二次候选 {tmdb.Candidates.Count} 个不符 → AwaitingReview",
                    });
                    MediaItemStatus oldRev = media.Status;
                    // 二次候选不符（零结果 / 多候选）：候选全集落库，多候选时供审核页直接单选，零结果时为空
                    media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
                    media.MarkAwaitingReview(rematchReason);
                    RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "TMDB 二次候选不符" });
                    await db.SaveChangesAsync(ct);
                    await NotifyAsync(media, oldRev, ct);
                    await EmitReviewCreatedAsync(media, ct);
                    _logger.LogInformation("二次 TMDB 候选不符 → AwaitingReview：{Path}", media.SourcePath);
                    return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
                }
            }
        }

        // 5. 采用 TMDB 候选 → 落 MediaItem → Classifying
        if (tmdb is null || tmdb.Candidates.Count == 0)
        {
            throw new InvalidOperationException("解析流程异常：无 TMDB 候选可用却未走 AwaitingReview");
        }

        // 5.1 候选四维择优（修复：此前盲取 Candidates[0] = TMDB 服务端默认序，同名不同剧 / 重制版常排错）：
        // 按「标题相似度 / 年份差 / 热度 / 语言产地」加权打分重排，把最佳候选放到首位再走既有流程
        // （不改 ParseTask 决策签名）。复用路径（reusedFromCache）与强制匹配（forcedMatch）跳过：
        // 合成候选来自本地既定绑定 / 用户指定 id，已是确定结果，并非搜索结果集，无需择优也不应被打分门槛误伤。
        IReadOnlyList<TmdbCandidateScore>? ranked = null;
        double? topScore = null;
        if (!reusedFromCache && !forcedMatch)
        {
            // 标题比对集合：有效解析标题（AI 优先）+ 规则原始标题 + 命中检索的别名（别名命中时
            // TMDB 主条目常是原名，与中文解析名相似度天然低，必须把真正搜中的词纳入比对取较高者）
            string?[] parsedTitles = [aiResult?.Title ?? rule.Title, rule.Title, matchedSearchAlias];
            ranked = TmdbCandidateScorer.Rank(
                tmdb.Candidates, parsedTitles, aiResult?.Year ?? rule.Year, scoreWeights, preferredLanguage);
            tmdb = tmdb with { Candidates = ranked.Select(r => r.Candidate).ToList() };
            topScore = ranked[0].Score;

            // 综合得分门槛：多候选最高分 < 0.5 视为无法可信取舍；单候选放宽到 0.35（防残缺标题模糊命中
            // 唯一一条错误结果被直接采纳）。低于门槛 → 候选全集落库转人工审核（复用多候选审核原因与 UX）。
            double minScore = tmdb.Candidates.Count > 1 ? MultiCandidateMinScore : SingleCandidateMinScore;
            if (topScore.Value < minScore)
            {
                RecordExit(media.Status, new
                {
                    query = aiResult is not null ? $"{aiResult.Title} + {aiResult.MediaType} + {aiResult.Year}" : $"{rule.Title} + {rule.MediaType} + {rule.Year}",
                    source = TmdbSourceLabel(),
                    candidates = ProjectCandidates(tmdb),
                    scores = ranked.Select(r => new { tmdbId = r.Candidate.Id, score = Math.Round(r.Score, 3) }).ToArray(),
                    decision = $"候选最高综合得分 {topScore.Value:F2} < 门槛 {minScore:F2}（四维加权：标题/年份/热度/语言）→ AwaitingReview",
                });
                MediaItemStatus oldScore = media.Status;
                media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
                media.MarkAwaitingReview(ReviewReason.TmdbMultiCandidate);
                RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "TMDB 候选综合得分低于门槛，无法自动取舍" });
                await db.SaveChangesAsync(ct);
                await NotifyAsync(media, oldScore, ct);
                await EmitReviewCreatedAsync(media, ct);
                _logger.LogInformation("TMDB 候选最高综合得分 {Score:F2} 低于门槛 {Min:F2} → AwaitingReview：{Path}",
                    topScore.Value, minScore, media.SourcePath);
                return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
            }
        }

        TmdbCandidate top = tmdb.Candidates[0];
        ParsedInfo parsedInfo = ParsedInfo.CreateFromOverride(
            title: aiResult?.Title ?? rule.Title,
            year: aiResult?.Year ?? rule.Year ?? top.Year,  // TMDB 候选 year 兜底（剧集常无 rule.year）
            mediaType: aiResult?.MediaType ?? rule.MediaType,
            season: aiResult?.Season ?? rule.Season,
            episode: aiResult?.Episode ?? rule.Episode,
            episodeEnd: aiResult?.EpisodeEnd ?? rule.EpisodeEnd,
            matchedRuleId: rule.MatchedRuleId,
            seasonTitle: rule.SeasonTitle) with  // 篇章季标题（AI 不产，取规则结果）供审核页对照
        {
            // 剧集组翻译时透传翻译前的源文件名命名空间季集，供归档阶段字幕归属匹配（非剧集组路径三者均 null，等价不设置）
            OriginalSeason = originalSeason,
            OriginalEpisode = originalEpisode,
            OriginalEpisodeEnd = originalEpisodeEnd,
        };
        media.ApplyTmdbMatch(
            tmdbId: top.Id,
            tmdbMediaType: top.MediaType,
            parseSource: forcedMatch ? ParseSource.Manual : (reusedFromCache ? ParseSource.Hybrid : (aiResult is not null ? ParseSource.Ai : ParseSource.Rule)),
            confidence: aiResult?.Confidence ?? rule.Confidence,
            parsedInfo: parsedInfo);

        // 非复用时回写文件夹缓存：把本次锁定的 series（剧名 + 类型 + TMDBid）留给同目录后续文件复用，跳过其 AI 兜底。
        // 仅 TV 才锁定：电影没有「分集」语义，且同一目录可能混放多部电影，锁定会让后续 AI 路径文件张冠李戴。
        // 强制匹配（forcedMatch）不回写：同目录后续文件本就各自命中同一 pmm.txt 重新锚定（详情走缓存，开销极小），
        // 无需经文件夹缓存的标题相似度复用，避免与"标识优先"语义混淆。
        if (!reusedFromCache && !forcedMatch && folderKey is not null
            && string.Equals(top.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            _folderCache.Set(folderKey, new FolderSeriesEntry(
                TmdbId: top.Id,
                MediaType: top.MediaType,
                Title: parsedInfo.Title,
                Year: parsedInfo.Year ?? top.Year,
                Confidence: aiResult?.Confidence ?? rule.Confidence));
        }

        // 进 Classifying 之前的 stage 是 TmdbMatching 或 TmdbRematching
        MediaItemStatus tmdbStage = media.Status;

        // 剧集解析完整性守护：season + episode 必须齐全（ArchiveService 缺其一会抛 BusinessException → Failed）
        // 上游 RuleEngineService 评分表已让缺字段的剧集压到 0.50 走 AI 兜底；本守护是 AI 兜底也未能回填时的
        // 最后一道关卡：不让无法 Plex 命名的剧集继续往 Archive 走，而是转 AwaitingReview 由用户在审核界面补全。
        bool isTvForArchive = string.Equals(top.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        // 单季自动补季：剧集仅缺季号（集号在）且 TMDB 仅 1 季 → 默认第 1 季继续归档，避免无谓人工兜底。
        // 集号无法靠 TMDB 反推（不知道是第几集），故只在「季缺、集在」时尝试；多季仍交人工选季。
        // 特别篇守护：解析标题 / 文件名带 OVA / SP / 特别篇等标记时禁用自动补季——特别篇在 Plex / TMDB
        // 语义里归 Season 00，补成 S01 会把 OVA / SP 当正片第 1 季归档错位；跳过后交由下方完整性守护转人工审核定季。
        bool specialEpisodeMarker = isTvForArchive && parsedInfo.Season is null
            && HasSpecialEpisodeMarker(parsedInfo.Title, rule.Title, media.FileName);
        if (specialEpisodeMarker)
        {
            _logger.LogInformation("解析标题/文件名含特别篇标记（OVA/SP/特别篇等），禁用单季自动补季：{Path}", media.SourcePath);
        }
        if (isTvForArchive && parsedInfo.Season is null && parsedInfo.Episode is not null && !specialEpisodeMarker)
        {
            int? totalSeasons = null;
            try
            {
                TmdbDetailsResult? details = await _tmdb.GetDetailsAsync(top.Id, top.MediaType, ct);
                totalSeasons = details?.TotalSeasons;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "查询 TMDB 季数失败 tmdbId={TmdbId}，跳过单季自动补季", top.Id);
            }
            if (totalSeasons == 1)
            {
                parsedInfo = parsedInfo with { Season = 1 };
                media.ApplyTmdbMatch(
                    tmdbId: top.Id,
                    tmdbMediaType: top.MediaType,
                    parseSource: forcedMatch ? ParseSource.Manual : (reusedFromCache ? ParseSource.Hybrid : (aiResult is not null ? ParseSource.Ai : ParseSource.Rule)),
                    confidence: aiResult?.Confidence ?? rule.Confidence,
                    parsedInfo: parsedInfo);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("剧集缺季号但 TMDB 仅 1 季 → 自动定为 S01：{Path}", media.SourcePath);
            }
        }

        if (isTvForArchive && (parsedInfo.Season is null || parsedInfo.Episode is null))
        {
            RecordExit(tmdbStage, new
            {
                query = aiResult is not null ? $"{aiResult.Title} + {aiResult.MediaType} + {aiResult.Year}" : $"{rule.Title} + {rule.MediaType} + {rule.Year}",
                source = TmdbSourceLabel(),
                candidates = ProjectCandidates(tmdb),
                picked = new { tmdbId = top.Id, title = top.Title, year = top.Year, mediaType = top.MediaType },
                extracted = new { title = parsedInfo.Title, year = parsedInfo.Year, season = parsedInfo.Season, episode = parsedInfo.Episode },
                decision = $"剧集解析字段不全 season={parsedInfo.Season?.ToString() ?? "null"} / episode={parsedInfo.Episode?.ToString() ?? "null"} → AwaitingReview",
            });
            MediaItemStatus oldGuard = media.Status;
            // 剧集字段不全：候选全集落库（已取 top 匹配，但保留全集便于用户换选）
            media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
            media.MarkAwaitingReview(ReviewReason.ParseIncomplete);
            RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "剧集字段不全，需用户补全 season / episode" });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(media, oldGuard, ct);
            await EmitReviewCreatedAsync(media, ct);
            _logger.LogInformation("剧集字段不全 → AwaitingReview：{Path} (season={Season}, episode={Episode})",
                media.SourcePath, parsedInfo.Season, parsedInfo.Episode);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
        }

        RecordExit(tmdbStage, new
        {
            query = aiResult is not null ? $"{aiResult.Title} + {aiResult.MediaType} + {aiResult.Year}" : $"{rule.Title} + {rule.MediaType} + {rule.Year}",
            source = TmdbSourceLabel(),
            aliasMatched = matchedSearchAlias,
            candidates = ProjectCandidates(tmdb),
            scores = ranked?.Select(r => new { tmdbId = r.Candidate.Id, score = Math.Round(r.Score, 3) }).ToArray(),
            picked = new { tmdbId = top.Id, title = top.Title, year = top.Year, mediaType = top.MediaType },
            decision = crossCheckNote is not null
                ? crossCheckNote
                : matchedSearchAlias is not null
                    ? (aliasFromLocal
                        ? $"主标题搜索不中，本地备选标题「{matchedSearchAlias}」命中（未动用 AI；候选 {tmdb.Candidates.Count} ≤ N={candidateThreshold}，综合得分 {topScore:F2}）"
                        : $"中文名二次搜索不中，AI 别名「{matchedSearchAlias}」命中（候选 {tmdb.Candidates.Count} ≤ N={candidateThreshold}，综合得分 {topScore:F2}）")
                    : reusedFromCache
                        ? "复用本地剧集映射合成单候选，直接采用"
                        : $"候选 {tmdb.Candidates.Count} ≤ N (={candidateThreshold})，按四维加权打分（标题/年份/热度/语言）取最高分 {topScore:F2}",
        });
        media.Transition(MediaItemStatus.Classifying);
        await db.SaveChangesAsync(ct);

        ClassifyResult cls = await _classify.ClassifyAsync(media, ct);
        if (cls.Decision == ClassifyDecision.SendToReview)
        {
            RecordExit(MediaItemStatus.Classifying, new { decision = "无规则命中 → AwaitingReview" });
            MediaItemStatus oldCls = media.Status;
            // 分类规则均未命中 → AwaitingReview，原因 CategoryUnresolved；候选全集落库便于用户复核换选
            media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
            media.MarkAwaitingReview(ReviewReason.CategoryUnresolved);
            RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "分类无命中" });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(media, oldCls, ct);
            await EmitReviewCreatedAsync(media, ct);
            _logger.LogInformation("分类无命中 → AwaitingReview：{Path}", media.SourcePath);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
        }
        if (cls.CategoryId is null)
            throw new InvalidOperationException("分类决策异常：Decision=Matched 但 CategoryId 为 null");
        media.AssignCategory(cls.CategoryId.Value);

        // 归档前拦截开关（Archive_HoldBeforeArchive）：开启时命中分类的高置信记录也不直接归档，
        // 改放入「人工确认」队列，由用户核对去向后点确认再归档——从源头拦下自动跑错。候选全集落库供复核换选。
        if (await ReadHoldBeforeArchiveAsync(db, ct))
        {
            RecordExit(MediaItemStatus.Classifying, new { categoryId = cls.CategoryId.Value, decision = "命中分类规则，但归档前拦截开启 → AwaitingReview" });
            MediaItemStatus oldHold = media.Status;
            media.SetTmdbCandidates(SerializeReviewCandidates(tmdb));
            media.MarkAwaitingReview(ReviewReason.HoldBeforeArchive);
            RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "归档前拦截：自动匹配已就绪，待人工确认后归档" });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(media, oldHold, ct);
            await EmitReviewCreatedAsync(media, ct);
            _logger.LogInformation("归档前拦截开启 → AwaitingReview：{Path}", media.SourcePath);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
        }
        RecordExit(MediaItemStatus.Classifying, new { categoryId = cls.CategoryId.Value, decision = "命中分类规则 → Archiving" });
        media.Transition(MediaItemStatus.Archiving);
        await db.SaveChangesAsync(ct);

        // 6.0 音频不兼容轨探测 + 处理决策（av3a Audio Vivid 等）：归档前探测源文件音轨，回写 MediaItem（History 打标），
        //     并据设置决定是否构造重混计划交归档层就近目标盘 ffmpeg 流复制丢轨（详见 ResolveAudioDecisionAsync）。
        AudioProcessDecision audio = await ResolveAudioDecisionAsync(db, media, ct);

        // 6. 归档：无重混计划走原 2 参重载（行为与改动前一字不差）；有重混计划走 3 参重载，
        //    归档第 6 步改用 ffmpeg 流复制丢不兼容音轨直接输出到目标。
        ArchiveResult arc = audio.RemuxPlan is null
            ? await _archive.ArchiveAsync(media, ct)
            : await _archive.ArchiveAsync(media, ArchiveOperation.Move, audio.RemuxPlan, ct);
        if (arc.Outcome == ArchiveOutcome.ConflictPending)
        {
            // 同名冲突 + 冲突策略「询问(Ask)」：不做任何文件操作，转入待确认队列由人工裁定是否覆盖（复用 NameCollision 原因）。
            // 与 ConflictSkipped 同样绝不写本记录 TargetPath（冲突目标是他人产物）；候选快照此前已随分类流程留存。
            RecordExit(MediaItemStatus.Archiving, new { operation = "MOVE", source = media.SourcePath, target = arc.TargetPath, conflict = true, decision = "同名冲突 → 待确认（询问策略）", audio = audio.Detail });
            MediaItemStatus oldReview = media.Status;
            media.MarkAwaitingReview(ReviewReason.NameCollision);
            RecordTerminal(MediaItemStatus.AwaitingReview, new { reason = "目标已存在同名文件，待人工确认是否覆盖", conflictTarget = arc.TargetPath });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(media, oldReview, ct);
            await EmitReviewCreatedAsync(media, ct);
            _logger.LogInformation("归档同名冲突（询问策略）→ AwaitingReview：{Path}（冲突目标 {Target}）", media.SourcePath, arc.TargetPath);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.AwaitingReview);
        }
        if (arc.Outcome == ArchiveOutcome.ConflictSkipped)
        {
            // 同名冲突：目标位置的文件是「他人产物」（本记录未做任何文件操作），绝不写入本记录 TargetPath——
            // 否则启动恢复（Archiving 孤儿按 TargetPath 文件存在推进 Completed）与撤销归档（按 TargetPath 反向移动）
            // 都会把他人文件误当本记录归档产物处理。冲突目标仅记入时间线步骤 detail 供排查。
            // 内容去重不受影响：去重只读 Completed 记录的 TargetPath，ConflictSkipped 落 Skipped 终态不参与比对。
            RecordExit(MediaItemStatus.Archiving, new { operation = "MOVE", source = media.SourcePath, target = arc.TargetPath, conflict = true, audio = audio.Detail });
            MediaItemStatus oldSk = media.Status;
            media.Transition(MediaItemStatus.Skipped);
            RecordTerminal(MediaItemStatus.Skipped, new { reason = "目标已存在同名文件", conflictTarget = arc.TargetPath });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(media, oldSk, ct);
            await EmitSkippedAsync(media, "目标已存在同名文件", ct);
            _logger.LogWarning("归档同名冲突 → Skipped：{Path}（冲突目标 {Target} 不计为本记录产物）", media.SourcePath, arc.TargetPath);
            return new ProcessFileOutcome(media.Id, ProcessOutcome.Skipped);
        }

        // 视频已实际落地：先用单独小事务持久化 TargetPath，再走警告 / 终态流程。
        // 崩溃窗口防护：若 Move 完成后、终态落库前进程崩溃，重启时 StartupRecoveryWorker 凭
        // 「Archiving + TargetPath 指向文件存在」恢复为 Completed；若 TargetPath 与 Completed 同一事务提交，
        // 窗口内 TargetPath=null 会被误判「归档未完成」→ Failed（文件实际已落地、源已删，Rescan 也救不回）。
        media.SetArchiveResult(arc.TargetPath);
        await db.SaveChangesAsync(ct);

        // 若 nfo/Webhook 失败（arc.Warnings 非空），时间线标记「待补元数据」但绝不判 Failed
        bool metadataPending = arc.Warnings is { Count: > 0 };
        RecordExit(MediaItemStatus.Archiving, new
        {
            operation = audio.RemuxPlan is not null ? "REMUX" : "MOVE",
            source = media.SourcePath,
            target = arc.TargetPath,
            audio = audio.Detail, // 音频探测/处理摘要（null 时序列化省略）
            metadataPending = metadataPending ? true : (bool?)null, // 省 null：无待补时不落该字段
            warnings = metadataPending ? arc.Warnings : null,
        });
        MediaItemStatus oldCmp = media.Status;
        media.Transition(MediaItemStatus.Completed);
        RecordTerminal(MediaItemStatus.Completed, new { target = media.TargetPath });
        await db.SaveChangesAsync(ct);
        await NotifyAsync(media, oldCmp, ct);
        if (metadataPending)
            _logger.LogWarning("归档完成但元数据待补 → Completed：{Source} → {Target}（待补：{Warnings}）",
                media.SourcePath, media.TargetPath, string.Join("；", arc.Warnings!));
        else
            _logger.LogInformation("归档完成 → Completed：{Source} → {Target}", media.SourcePath, media.TargetPath);
        return new ProcessFileOutcome(media.Id, ProcessOutcome.Completed);
    }

    /// <summary>读「归档前拦截」开关（Archive_HoldBeforeArchive）：缺失 / 非 "true" 一律 false（默认不拦截，行为同改动前）</summary>
    private static async Task<bool> ReadHoldBeforeArchiveAsync(PmmDbContext db, CancellationToken ct)
    {
        string? raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == HoldBeforeArchiveKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 音频不兼容轨探测 / 处理决策 ----------

    /// <summary>归档前音频探测 + 不兼容轨（av3a 等）处理决策</summary>
    /// <remarks>
    /// 串行子步骤（守红线：本服务内不并发 EF）：读 Audio_* 设置 → 解析 ffprobe/ffmpeg 路径 → 探测源文件音轨 →
    /// 回写 media.SetAudioProbe（History 打标）→ 据「是否含不兼容轨 + 是否开启自动重混 + 是否有兼容轨可保留」
    /// 决定返回重混计划（交归档层执行）或仅标记。任何缺工具 / 探测不可用一律降级为「不重混」，绝不阻断归档。
    /// 返回的 Detail 会被并进 Archiving 步骤 detail 供时间线展示。
    /// </remarks>
    private async Task<AudioProcessDecision> ResolveAudioDecisionAsync(PmmDbContext db, MediaItem media, CancellationToken ct)
    {
        Dictionary<string, string?> s = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == AudioProbeSupport.CheckEnabledKey || x.Key == AudioProbeSupport.FfmpegPathKey
                     || x.Key == AudioProbeSupport.IncompatibleCodecsKey || x.Key == AudioProbeSupport.AutoRemuxKey)
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);

        // 总开关关闭：不探测、不标记、不重混
        if (!AudioProbeSupport.IsSettingTrue(s, AudioProbeSupport.CheckEnabledKey))
            return AudioProcessDecision.None;

        // 不兼容 codec 清单（默认 av3a；清空 = 不检查任何 codec）
        IReadOnlySet<string> codecs = AudioProbeSupport.ParseCodecList(AudioProbeSupport.GetSetting(s, AudioProbeSupport.IncompatibleCodecsKey));
        if (codecs.Count == 0)
            return AudioProcessDecision.None;

        // 解析 ffprobe / ffmpeg 可执行文件路径（Audio_FfmpegPath 可为安装目录或某个 exe 全路径）
        (string? ffprobe, string? ffmpeg) = AudioProbeSupport.ResolveFfmpegTools(AudioProbeSupport.GetSetting(s, AudioProbeSupport.FfmpegPathKey));
        if (ffprobe is null)
            return new AudioProcessDecision(null, new { skipped = "未配置 ffmpeg 路径或 ffprobe 不存在" });

        // 探测源文件音轨
        AudioProbeResult probe = await _audioProbe.ProbeAsync(ffprobe, media.SourcePath, codecs, ct);
        if (!probe.Available)
            return new AudioProcessDecision(null, new { skipped = probe.Error });

        // 回写探测结果（History 打标）
        string? codecsCsv = probe.Streams.Count > 0
            ? string.Join(",", probe.Streams.Select(x => x.Codec).Where(c => c.Length > 0))
            : null;
        media.SetAudioProbe(codecsCsv, probe.HasIncompatible);
        await db.SaveChangesAsync(ct);

        // 无不兼容轨：仅记录 codec 快照，不重混
        if (!probe.HasIncompatible)
            return new AudioProcessDecision(null, new { audioCodecs = codecsCsv, incompatible = false });

        string[] incompatNames = probe.Streams.Where(x => x.IsIncompatible)
            .Select(x => x.Codec).Distinct().ToArray();

        // 未开启自动重混：仅标记，让用户自行处理
        if (!AudioProbeSupport.IsSettingTrue(s, AudioProbeSupport.AutoRemuxKey))
            return new AudioProcessDecision(null,
                new { audioCodecs = codecsCsv, incompatible = true, incompatibleCodecs = incompatNames, action = "mark-only" });

        // 自动重混前置守门：必须有兼容音轨可保留（否则丢光变无声）+ ffmpeg 可用
        if (!probe.HasCompatible)
            return new AudioProcessDecision(null,
                new { audioCodecs = codecsCsv, incompatible = true, incompatibleCodecs = incompatNames, action = "skip-remux：无兼容音轨可保留" });
        if (ffmpeg is null)
            return new AudioProcessDecision(null,
                new { audioCodecs = codecsCsv, incompatible = true, incompatibleCodecs = incompatNames, action = "skip-remux：ffmpeg 不存在" });

        // 构造重混计划：丢弃全部不兼容音轨的全局流索引
        IReadOnlyList<int> dropIdx = probe.IncompatibleStreamIndexes;
        return new AudioProcessDecision(
            new AudioRemuxPlan(ffmpeg, dropIdx),
            new { audioCodecs = codecsCsv, incompatible = true, incompatibleCodecs = incompatNames, action = "remux-drop", droppedStreams = dropIdx });
    }

    /// <summary>音频处理决策结果（重混计划 + 时间线 detail 摘要）</summary>
    /// <remarks>RemuxPlan 非 null 表示需归档层 ffmpeg 重混丢轨；Detail 并入 Archiving 步骤 detail（null 则序列化省略）。</remarks>
    private sealed record AudioProcessDecision(AudioRemuxPlan? RemuxPlan, object? Detail)
    {
        /// <summary>不处理（开关关 / 清单空）：无重混、无 detail</summary>
        public static readonly AudioProcessDecision None = new(null, null);
    }

    /// <summary>步骤 JSON 序列化配置（camelCase，省 null 字段保持 detail 紧凑；CJK 不转义保可读）</summary>
    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // 默认 STJ 把 CJK 转 \uXXXX，detail 落库后审计/前端展示不可读 — 切到 UnsafeRelaxed 保中文原样
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把 TmdbCandidate 数组投影成精简对象用于 Step.Detail（避免落入海量字段）</summary>
    private static object[] ProjectCandidates(TmdbSearchResult? tmdb)
    {
        if (tmdb is null) return Array.Empty<object>();
        return tmdb.Candidates.Select(c => (object)new
        {
            tmdbId = c.Id,
            title = c.Title,
            year = c.Year,
            mediaType = c.MediaType,
        }).ToArray();
    }

    /// <summary>审核候选快照上限：取相似度最高前 N 个，防「both」多候选极端膨胀 TmdbCandidatesJson（SQLite 下 maxlength 不强制）</summary>
    private const int MaxReviewCandidates = 20;

    /// <summary>把 TMDB 候选投影为审核候选快照 JSON（落 Media_Item.TmdbCandidatesJson，供审核页单选）</summary>
    /// <remarks>null / 空候选返回 null；取列表前 MaxReviewCandidates 个（经四维打分重排时即综合得分最高的前 N 个）；与 ProjectCandidates（仅供时间线 Step.Detail）区别在于字段更全且持久化供人工选择。</remarks>
    private static string? SerializeReviewCandidates(TmdbSearchResult? tmdb)
    {
        if (tmdb is null || tmdb.Candidates.Count == 0) return null;
        return ReviewCandidateSnapshot.Serialize(tmdb.Candidates.Take(MaxReviewCandidates).Select(c => new ReviewCandidateSnapshot(
            c.Id, c.MediaType, c.Title, c.OriginalTitle, c.Year, c.PosterPath)).ToList());
    }

    /// <summary>调 ITaskNotifier 广播状态扭转事件；推送失败由实现侧吞咽，不影响业务流</summary>
    private Task NotifyAsync(MediaItem media, MediaItemStatus oldStatus, CancellationToken ct) =>
        _notifier.NotifyStatusChangedAsync(new TaskStatusChangedEvent(
            media.Id, media.FileName, oldStatus, media.Status, DateTimeOffset.UtcNow), ct);

    /// <summary>发 media.failed Webhook（处理失败终态）；受总开关 gated + 实现侧自吞异常，不阻断主流程</summary>
    private Task EmitFailedAsync(MediaItem media, string error, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaFailed, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            error,
        }, ct);

    /// <summary>发 media.skipped Webhook（内容去重命中 / 归档同名冲突）；targetPath 取本记录归档产物（冲突时为 null，冲突目标是他人文件不外发）</summary>
    private Task EmitSkippedAsync(MediaItem media, string reason, CancellationToken ct) =>
        _webhook.EmitAsync(WebhookEvents.MediaSkipped, new
        {
            mediaItemId = media.Id,
            sourcePath = media.SourcePath,
            fileName = media.FileName,
            targetPath = media.TargetPath,
            reason,
        }, ct);

    /// <summary>发 review.created Webhook（进入人工确认队列）；ReviewReason 此刻已由 MarkAwaitingReview 赋值</summary>
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

    private static MediaItem CreateMediaItem(string sourcePath)
    {
        FileInfo fi = new(sourcePath);
        long fileSize = fi.Exists ? fi.Length : 0;
        string fileName = fi.Name;
        return MediaItem.CreateDetected(sourcePath, fileName, fileSize);
    }

    private static string? TryParentFolderName(string fullPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(fullPath);
            return dir is null ? null : Path.GetFileName(dir);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取文件所在目录完整路径作为文件夹级缓存键；无法取得返回 null</summary>
    private static string? TryFolderKey(string sourcePath)
    {
        try { return Path.GetDirectoryName(sourcePath); }
        catch { return null; }
    }

    /// <summary>文件夹级 series 复用守门：缓存条目须为 TV 且与本文件规则标题足够匹配才允许复用</summary>
    /// <remarks>
    /// 电影各自独立（同目录可能多部电影）一律不复用——配合写入侧 TV-only 守门，缓存里本就不该有电影；
    /// TV 则要求本文件规则标题与缓存剧名「相似度 ≥ 阈值 或 归一化后互为子串」（见 TitleMatchesForReuse），
    /// 避免同一子目录混放不同剧时把后者套成前者。不满足则回落正常 AI/TMDB 流程（最坏多调一次 AI，绝不污染结果）。
    /// </remarks>
    private static bool CanReuseFolderSeries(FolderSeriesEntry cached, RuleParseResult rule) =>
        string.Equals(cached.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
        && TitleMatchesForReuse(rule.Title, cached.Title);

    /// <summary>复用守门标题匹配：相似度达标 OR 归一化后互为子串（双语混排名兜底）</summary>
    /// <remarks>
    /// 仅按 Levenshtein 相似度守门时，双语混排规则标题（如「国王排名 Ousama Ranking」）与 AI 清洗后的
    /// 缓存剧名（「国王排名」）相似度仅 ≈ 0.22 → 复用恒不命中、同剧每集都白烧一次 AI。
    /// 兜底规则：归一化（去空白 + 小写）后任一方为另一方子串（含前缀）即视为同剧——双向比对，
    /// 覆盖「规则名混排含缓存名」与「缓存名混排含规则名」两个方向；双方长度均须 ≥ 2，
    /// 防「A」这类碎片标题误中一切。不改 FolderSeriesCache 结构。
    /// </remarks>
    private static bool TitleMatchesForReuse(string? ruleTitle, string? cachedTitle)
    {
        if (TitleSimilarity.Ratio(ruleTitle, cachedTitle) >= FolderReuseTitleSimilarityThreshold)
            return true;

        string normalizedRule = NormalizeTitleForReuse(ruleTitle);
        string normalizedCached = NormalizeTitleForReuse(cachedTitle);
        if (normalizedRule.Length < 2 || normalizedCached.Length < 2)
            return false;
        return normalizedRule.Contains(normalizedCached, StringComparison.Ordinal)
            || normalizedCached.Contains(normalizedRule, StringComparison.Ordinal);
    }

    /// <summary>复用守门用标题归一化：去全部空白 + 小写（与 TitleSimilarity 的归一化口径一致）</summary>
    private static string NormalizeTitleForReuse(string? s) =>
        string.IsNullOrEmpty(s)
            ? string.Empty
            : new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    /// <summary>特别篇 CJK 标记词表：解析标题 / 文件名含任一标记 → 禁用单季自动补季（OVA / SP 不是正片第 1 季）</summary>
    private static readonly string[] SpecialEpisodeCjkMarkers =
        ["特别篇", "特別篇", "特典", "番外", "总集篇", "總集篇"];

    /// <summary>特别篇 ASCII 标记（OVA / OAD / SPECIAL / SP+可选数字），词边界匹配防 Spider / Spy 等普通词误中</summary>
    private static readonly Regex SpecialEpisodeAsciiMarker =
        new(@"\b(?:OVA|OAD|SPECIAL|SP)\d*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>判断任一文本（解析标题 / 规则标题 / 文件名）是否带特别篇标记</summary>
    private static bool HasSpecialEpisodeMarker(params string?[] texts)
    {
        foreach (string? text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (SpecialEpisodeAsciiMarker.IsMatch(text)) return true;
            foreach (string marker in SpecialEpisodeCjkMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>合成单候选 TmdbSearchResult（复用本地剧集映射、跳过 TMDB 搜索时构造）</summary>
    /// <remarks>仅携带 series 维度字段（tmdbId / 类型 / 剧名 / 年份）；季 / 集由本文件规则结果提供，RawJson 留空。</remarks>
    private static TmdbSearchResult SynthesizeSeriesCandidate(FolderSeriesEntry s) =>
        new(
            [new TmdbCandidate(s.TmdbId, s.MediaType, s.Title,
                OriginalTitle: null, s.Year, Popularity: 0, OriginalLanguage: null,
                OriginCountry: null, PosterPath: null, Overview: null)],
            RawJson: null);

    /// <summary>按强制匹配标识拉取 TMDB 详情并确定类型；两种类型都失败返回 null（调用方据此转人工）</summary>
    /// <remarks>
    /// pmm.txt / TMDB URL 标识自带类型（forced.MediaType 非空）→ 只按该类型拉一次，失败即 null（与改造前一致）。
    /// 文件夹名 {tmdb-NNN} 标识只给 id、不锁类型（forced.MediaType 为 null）→ 先用规则识别出的类型拉，
    /// 失败（多半是 tv/movie 猜反，TMDB 的 tv/{id} 与 movie/{id} 是两个独立命名空间，类型错则 404）再翻另一类型兜底。
    /// 规则类型为 "unknown" 时按 tv 优先尝试、再 movie。返回 (详情, 实际命中类型)。
    /// </remarks>
    private async Task<(TmdbDetailsResult Details, string ResolvedType)?> FetchForcedDetailsAsync(
        ForcedMatchMarker forced, string ruleMediaType, string sourcePath, CancellationToken ct)
    {
        string[] attempts;
        if (forced.MediaType is string lockedType)
        {
            attempts = [lockedType];
        }
        else
        {
            string ruleType = string.Equals(ruleMediaType, "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "tv";
            attempts = ruleType == "movie" ? ["movie", "tv"] : ["tv", "movie"];
        }

        TmdbClientException? lastError = null;
        for (int i = 0; i < attempts.Length; i++)
        {
            try
            {
                TmdbDetailsResult details = await _tmdb.GetDetailsAsync(forced.TmdbId, attempts[i], ct);
                if (i > 0)
                    _logger.LogInformation(
                        "文件夹名强制匹配 id={TmdbId}：规则类型 {First} 拉取失败，已回退 {Resolved}",
                        forced.TmdbId, attempts[0], attempts[i]);
                return (details, attempts[i]);
            }
            catch (TmdbClientException ex)
            {
                lastError = ex;
            }
        }
        _logger.LogWarning(lastError,
            "强制匹配标识 TMDB id={TmdbId} 详情拉取失败（已尝试类型：{Types}）→ AwaitingReview：{Path}",
            forced.TmdbId, string.Join("/", attempts), sourcePath);
        return null;
    }

    /// <summary>合成强制匹配单候选：用 TMDB 详情填充真实标题/原名/年份/海报，进入既有"采用候选"段</summary>
    private static TmdbSearchResult SynthesizeForcedCandidate(int tmdbId, string mediaType, TmdbDetailsResult details) =>
        new(
            [new TmdbCandidate(tmdbId, mediaType.ToLowerInvariant(), details.Title,
                details.OriginalTitle, details.Year, Popularity: 0, details.OriginalLanguage,
                details.OriginCountry, details.PosterPath, details.Overview)],
            RawJson: null);

    /// <summary>拉取剧集组并定位目标分组，返回按 Order 升序的条目列表（"编组内位置 → 正典季集"映射表）；无法定位返回 null</summary>
    /// <remarks>
    /// 取分组（pmm.txt 的 group= 指定，或剧集组下仅一个分组时唯一），按 Order 升序排列；调用方按"文件第 N 集 →
    /// 编组内第 N 位（Order 0 起，索引 N-1）"翻译并自行做集号越界判断。多分组未指定 group / 指定 group 不存在 /
    /// 拉取失败均返回 null（调用方据此清空集号转人工）。返回整段后，起始集与双集末集复用同一份映射本地翻译，仅拉取一次。
    /// </remarks>
    private async Task<List<TmdbEpisodeGroupEntry>?> TryResolveEpisodeGroupSegmentAsync(ForcedMatchMarker forced, CancellationToken ct)
    {
        TmdbEpisodeGroup group;
        try
        {
            group = await _tmdb.GetEpisodeGroupAsync(forced.EpisodeGroupId!, ct);
        }
        catch (TmdbClientException ex)
        {
            _logger.LogWarning(ex, "拉取 TMDB 剧集组失败：{Eg}", forced.EpisodeGroupId);
            return null;
        }

        TmdbEpisodeGroupSegment? seg = forced.GroupId is not null
            ? group.Groups.FirstOrDefault(g => string.Equals(g.Id, forced.GroupId, StringComparison.OrdinalIgnoreCase))
            : group.Groups.Count == 1 ? group.Groups[0] : null;
        if (seg is null) return null; // 多分组但未指定 group，或指定的 group 不存在

        return seg.Episodes.OrderBy(e => e.Order).ToList();
    }

    /// <summary>从持久库还原同文件夹已归档剧集的 series 身份（复用以跳过 TMDB 搜索）</summary>
    /// <remarks>
    /// 取同文件夹（SourcePath 前缀匹配）下已成功归档（Completed / Skipped）的 TV 兄弟集，还原其
    /// tmdbId + 类型 + 解析剧名 / 年份为 FolderSeriesEntry，供 CanReuseFolderSeries 标题相似度守门后复用。
    /// 设计要点：
    ///   · 仅 TV —— 电影各自独立、不共享 series（与写入侧 TV-only 守门一致），从源头杜绝电影被误复用；
    ///   · 最近归档优先（ArchivedAt 降序，Skipped 无 ArchivedAt 自然排后）；
    ///   · 剧名 / 年份取自兄弟集 ParsedInfo（与本文件规则标题同域，便于相似度比对，避免 TMDB 规范名跨语言失配）；
    ///   · 前缀加目录分隔符，避免「Show」误匹配「Show 2」；
    ///   · 混放多剧子目录最坏返回不相似兄弟 → 守门拒绝复用、回落正常搜索流程（安全降级，绝不张冠李戴）；
    ///   · 仅在 L1 缓存未命中时调用（每文件夹每进程至多一次），AsNoTracking 不干扰主聚合跟踪。
    /// </remarks>
    private async Task<FolderSeriesEntry?> TryResolveSeriesFromDbAsync(PmmDbContext db, string folderKey, CancellationToken ct)
    {
        string prefix = folderKey.EndsWith(Path.DirectorySeparatorChar)
            ? folderKey
            : folderKey + Path.DirectorySeparatorChar;

        var sibling = await db.MediaItems.AsNoTracking()
            .Where(m => m.TmdbMediaType == "tv"
                     && m.TmdbId != null
                     && (m.Status == MediaItemStatus.Completed || m.Status == MediaItemStatus.Skipped)
                     && m.SourcePath.StartsWith(prefix))
            .OrderByDescending(m => m.ArchivedAt)
            .Select(m => new { m.TmdbId, Type = m.TmdbMediaType!, ParsedJson = m.ParsedInfo, m.Confidence })
            .FirstOrDefaultAsync(ct);

        if (sibling?.TmdbId is not null)
        {
            ParsedInfo? pi = ParsedInfo.FromJson(sibling.ParsedJson);
            return new FolderSeriesEntry(
                TmdbId: sibling.TmdbId.Value,
                MediaType: sibling.Type,
                Title: pi?.Title,
                Year: pi?.Year,
                Confidence: sibling.Confidence);
        }

        // 本目录无已归档兄弟集 → 兄弟目录兜底：追更下载器常「每集单开一个目录」
        // （剧名[第01集]xxx\ / 剧名[第02集]xxx\），精确目录键永不复用、每个目录首集都要重烧搜索/AI。
        // 取同父目录下最近归档的 TV 记录，目录名剥去集号段归一化后与本目录相同 → 视为同剧兄弟目录。
        // 命中后仍走 CanReuseFolderSeries 标题相似度守门，误配最坏回落正常搜索流程。
        return await TryResolveSeriesFromSiblingFolderAsync(db, folderKey, ct);
    }

    /// <summary>兄弟目录集号段剥离：第NN集/话/回（含方括号/全角括号包裹与区间）、SxxExx、E/EP+数字</summary>
    private static readonly Regex SiblingEpisodeSegment = new(
        // 环视而非 \b：.NET 里汉字属 \w，「剧名S01E01」的 S 前无词边界，\b 会漏剥紧贴中文的集号段
        @"[\[【(（]?\s*第\s*\d{1,4}(\s*[-~～]\s*\d{1,4})?\s*[集话話回]\s*[\]】)）]?"
        + @"|(?<![A-Za-z0-9])S\d{1,2}\s*E\d{1,4}(\s*-\s*E?\d{1,4})?(?![A-Za-z0-9])"
        + @"|(?<![A-Za-z0-9])EP?\.?\s*\d{1,4}(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>目录名归一化（兄弟目录匹配用）：剥集号段 + 去空白 + 小写</summary>
    /// <remarks>
    /// 同一父目录下两个目录原名必不同（文件系统约束），归一化后相等 ⇒ 仅集号段不同 ⇒ 分集目录模式，
    /// 不会把「Season 01」「Season 02」这类季目录互配（季号不在剥离模式内，归一化后仍不同）。
    /// </remarks>
    private static string NormalizeFolderNameForSiblingMatch(string name) =>
        new string(SiblingEpisodeSegment.Replace(name, " ").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    /// <summary>同父目录下按「目录名剥集号归一化相等」找已归档同剧兄弟目录并还原 series 身份</summary>
    /// <remarks>
    /// 仅比对相对父目录的第一段目录名（兄弟目录内部可能还有 Season 子层，SourcePath 的直接父目录不可靠）；
    /// 归一化后不足 2 字符（剥完只剩碎片）放弃匹配，防「01」「E02」这类纯集号目录误配一切；
    /// 最近归档优先、至多扫 50 条兄弟记录——仅 L1 缓存与精确目录键均未命中时才走到这里（每文件夹每进程至多一次）。
    /// </remarks>
    private async Task<FolderSeriesEntry?> TryResolveSeriesFromSiblingFolderAsync(PmmDbContext db, string folderKey, CancellationToken ct)
    {
        string? parentDir;
        try { parentDir = Path.GetDirectoryName(folderKey); }
        catch { return null; }
        if (string.IsNullOrEmpty(parentDir)) return null;

        string normalizedSelf = NormalizeFolderNameForSiblingMatch(Path.GetFileName(folderKey));
        if (normalizedSelf.Length < 2) return null;

        string parentPrefix = parentDir.EndsWith(Path.DirectorySeparatorChar)
            ? parentDir
            : parentDir + Path.DirectorySeparatorChar;

        var rows = await db.MediaItems.AsNoTracking()
            .Where(m => m.TmdbMediaType == "tv"
                     && m.TmdbId != null
                     && (m.Status == MediaItemStatus.Completed || m.Status == MediaItemStatus.Skipped)
                     && m.SourcePath.StartsWith(parentPrefix))
            .OrderByDescending(m => m.ArchivedAt)
            .Select(m => new { m.SourcePath, m.TmdbId, Type = m.TmdbMediaType!, ParsedJson = m.ParsedInfo, m.Confidence })
            .Take(50)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            string rel = row.SourcePath[parentPrefix.Length..];
            int cut = rel.IndexOfAny(['\\', '/']);
            if (cut <= 0) continue; // 文件直接平铺在父目录下，无兄弟目录段
            string siblingTopDir = rel[..cut];
            if (!string.Equals(NormalizeFolderNameForSiblingMatch(siblingTopDir), normalizedSelf, StringComparison.Ordinal))
                continue;

            ParsedInfo? pi = ParsedInfo.FromJson(row.ParsedJson);
            _logger.LogInformation(
                "兄弟目录同剧映射命中（分集目录模式）→ 复用 series 身份跳过搜索/AI：{Folder} ≈ {Sibling}，tmdbId={TmdbId}",
                folderKey, siblingTopDir, row.TmdbId);
            return new FolderSeriesEntry(
                TmdbId: row.TmdbId!.Value,
                MediaType: row.Type,
                Title: pi?.Title,
                Year: pi?.Year,
                Confidence: row.Confidence);
        }
        return null;
    }

    /// <summary>写入完成检测失败（源消失 / 写入超时）时把登记行终态化为 Failed，防止僵尸行滞留</summary>
    /// <remarks>
    /// 区分两种成因（探测器只返回 false，由 IFileProbe 复核源文件存在性区分）：
    ///   · 源文件已消失（被删除 / 移走 / 重命名）→ Failed，避免每次重启被重排后空轮询；
    ///   · 仍在写入超时（大文件复制 &gt; 300s）→ Failed，文案提示可重新扫描——Failed 行可被
    ///     History.Rescan 手动重投、也会被「强制全量扫描」自动重投，文件写完后即自动救回。
    /// 无登记行（理论上 FileIntakeService 必先建行）或已终态 → 维持旧行为按 Skipped 返回，不新建行。
    /// </remarks>
    private async Task<ProcessFileOutcome> FinalizeWriteDetectionFailureAsync(string fullPath, CancellationToken ct)
    {
        bool sourceMissing = !_fileProbe.FileExists(fullPath);
        string reason = sourceMissing
            ? "源文件已消失（写入完成检测时文件已不存在，可能被删除 / 移走 / 重命名）"
            : $"文件写入超时（{WriteCompletionTimeoutSeconds}s 内大小未稳定，可能仍在复制中），完成后可重新扫描";

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        MediaItem? media = await db.MediaItems.FirstOrDefaultAsync(m => m.SourcePath == fullPath, ct);
        if (media is null || media.IsTerminal())
        {
            _logger.LogWarning("文件写入未完成（{Reason}），无可终态化的登记行，跳过：{Path}", reason, fullPath);
            return new ProcessFileOutcome(media?.Id ?? 0, ProcessOutcome.Skipped);
        }

        await SafeMarkFailedAsync(db, media, reason, ct);
        _logger.LogWarning("文件写入未完成 → Failed（{Reason}）：{Path}", reason, fullPath);
        return new ProcessFileOutcome(media.Id, ProcessOutcome.Failed);
    }

    private async Task SafeMarkFailedAsync(
        PmmDbContext db,
        MediaItem media,
        string reason,
        CancellationToken ct)
    {
        try
        {
            if (!media.IsTerminal())
            {
                MediaItemStatus old = media.Status;
                media.MarkFailed(reason);
                // Failed 是终态，记一条 Step 让时间线有「失败收尾」
                media.AppendStep(MediaItemStatus.Failed, _clock.UtcNow, durMs: 0,
                    JsonSerializer.Serialize(new { reason, fromStage = old.ToString() }, StepJsonOptions));
                await db.SaveChangesAsync(ct);
                await NotifyAsync(media, old, ct);
                await EmitFailedAsync(media, reason, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "标记 MediaItem 为 Failed 时再次失败：MediaItemId={MediaItemId}", media.Id);
            // P2-r3.8：补偿失败必须向上抛而非静默吞咽
            // 不抛 → MediaItem 留在 Archiving 等非终态 + Outcome=Failed 误导上层「已收尾」；
            // 抛 → 让 TaskProcessorWorker 感知到「数据一致性破坏」，并依赖 StartupRecoveryWorker 下次启动清理。
            throw;
        }
    }
}
