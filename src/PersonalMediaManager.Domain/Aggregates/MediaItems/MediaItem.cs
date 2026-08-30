using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Aggregates.MediaItems;

/// <summary>媒体处理主记录聚合根（Media_Item，含状态机）</summary>
/// <remarks>
/// D1.1 落地完整状态机 Transition(MediaItemStatus next) + 14 状态合法转移表（需求文档 §6.2）；
/// 任何状态都可转 Failed（IO/网络/未预期异常）；Failed 仅允许由 History.Rescan 拉回 Queued。
/// SourcePath UQ 防重入；FileHash 索引可选用于内容去重；RowVersion 乐观并发由拦截器 +1。
/// </remarks>
public sealed class MediaItem : AggregateRoot
{
    /// <summary>EF Core 反射构造</summary>
    private MediaItem() { }

    /// <summary>聚合内时间线步骤集合（由 AppendStep 追加；EF Core 通过 navigation 持久化）</summary>
    private readonly List<ProcessStep> _steps = new();

    /// <summary>对外只读暴露处理时间线（按 StartedAt 排序，供 History 详情返回）</summary>
    public IReadOnlyCollection<ProcessStep> Steps => _steps.AsReadOnly();

    /// <summary>新发现文件入队工厂（FileWatcherWorker / ScanService 调用）</summary>
    public static MediaItem CreateDetected(string sourcePath, string fileName, long fileSize)
    {
        return new MediaItem
        {
            SourcePath = sourcePath,
            FileName = fileName,
            FileSize = fileSize,
            Status = MediaItemStatus.Detected,
        };
    }

    /// <summary>源文件绝对路径（UQ，防重复入队）</summary>
    public string SourcePath { get; private set; } = default!;

    public string FileName { get; private set; } = default!;

    public long FileSize { get; private set; }

    /// <summary>可选 SHA256 内容去重（暂未启用 hash 计算流程，预留字段）</summary>
    public string? FileHash { get; private set; }

    /// <summary>状态机当前值；外部禁止直接 set，必须走 Transition(next)</summary>
    public MediaItemStatus Status { get; private set; } = MediaItemStatus.Detected;

    public ParseSource? ParseSource { get; private set; }

    /// <summary>最终采纳的置信度（0~1）</summary>
    public double? Confidence { get; private set; }

    /// <summary>JSON：{title,type,year,season,episode,...}；由 ParsedInfo 值对象序列化</summary>
    public string? ParsedInfo { get; private set; }

    public int? TmdbId { get; private set; }

    /// <summary>movie / tv（小写 TMDB 原值）</summary>
    public string? TmdbMediaType { get; private set; }

    /// <summary>审核候选快照 JSON：解析阶段产生的全部 TMDB 候选，供人工确认页单选</summary>
    /// <remarks>
    /// 仅在转入 AwaitingReview 前由 ProcessFileService 调 SetTmdbCandidates 写入；
    /// 结构为候选数组（tmdbId/mediaType/title/originalTitle/year/posterPath），由 Service 层序列化。
    /// 与 TmdbId（最终采纳的单个）解耦：多候选场景 TmdbId 可能为 null，候选全集仍可在审核页展示。
    /// </remarks>
    public string? TmdbCandidatesJson { get; private set; }

    public long? CategoryId { get; private set; }

    /// <summary>归档后目标路径</summary>
    public string? TargetPath { get; private set; }

    /// <summary>失败原因（最新一次）</summary>
    public string? ErrorMessage { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>进入人工确认队列的原因（仅 Status==AwaitingReview 时有值，其它状态保持 null）</summary>
    /// <remarks>
    /// 由 MarkAwaitingReview(reason) 在转入 AwaitingReview 的同一动作内写入；
    /// 离开 AwaitingReview（confirm/ignore）时不清空，保留历史轨迹供 History 查询。
    /// </remarks>
    public ReviewReason? ReviewReason { get; private set; }

    /// <summary>归档/源文件最近一次存在性检查结果：true=文件已缺失</summary>
    /// <remarks>
    /// 由「扫描整库存在性」（LibraryService.ScanExistenceAsync）按 File.Exists(TargetPath) 写入，供媒体库列表/卡片打缺失标记。
    /// 仅持久化侧标注，不参与状态机；详情页的实时存在性由读取时即时计算（不落此列）。
    /// </remarks>
    public bool FileMissing { get; private set; }

    /// <summary>最近一次存在性检查时间（UTC，null=从未检查）</summary>
    public DateTimeOffset? FileCheckedAt { get; private set; }

    /// <summary>音轨编解码器快照（逗号分隔，如 "av3a,aac"；null=未探测 / 未启用音频检查）</summary>
    /// <remarks>归档前由 SetAudioProbe 写入（仅当启用音频检查 + ffmpeg 可用）；供 History 展示与排查，不参与状态机。</remarks>
    public string? AudioCodecs { get; private set; }

    /// <summary>是否含不兼容音轨（如 av3a Audio Vivid，Plex 等多数客户端无法解码）</summary>
    /// <remarks>由 SetAudioProbe 写入；前端据此打「不兼容音轨」徽标 + 列表筛选。</remarks>
    public bool HasIncompatibleAudio { get; private set; }

    /// <summary>处理过程是否动用过 AI 解析（「AI 参与度」统计的持久口径）</summary>
    /// <remarks>
    /// 由 ProcessFileService 在真正发起 AI 升级链调用时置位（仅统计真实调用——文件夹复用直通、
    /// 纯规则直查不算）；一经置位不清零，重投重跑也保留。与 Audit_AiCall 的区别：审计行受保留期
    /// 清理（90 天 / 行数上限）影响，本标记与 Media_Item 同生命周期，保证统计口径长期稳定。
    /// </remarks>
    public bool AiInvolved { get; private set; }

    /// <summary>合法状态转移表（需求文档 §6.2 核心流转）</summary>
    /// <remarks>
    /// 自循环（next == current）一律拒绝；Failed 由 Application 任何状态主动调 MarkFailed() 进入；
    /// 终态 Completed/Skipped/Ignored 不允许再转移；Failed 仅允许通过 History.Rescan 重置回 Queued。
    /// </remarks>
    private static readonly IReadOnlyDictionary<MediaItemStatus, IReadOnlySet<MediaItemStatus>> AllowedTransitions =
        new Dictionary<MediaItemStatus, IReadOnlySet<MediaItemStatus>>
        {
            // Detected：写入完成 → Queued；忽略规则命中 → Skipped；用户取消 → Cancelled
            [MediaItemStatus.Detected] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Queued,
                MediaItemStatus.Skipped,
                MediaItemStatus.Cancelled,
            },
            // Queued：处理器取走 → Parsing
            [MediaItemStatus.Queued] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Parsing,
                MediaItemStatus.Cancelled,
            },
            // Parsing：置信度≥阈值 → TmdbMatching；<阈值或特殊字符 → AiParsing
            [MediaItemStatus.Parsing] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.TmdbMatching,
                MediaItemStatus.AiParsing,
                MediaItemStatus.Cancelled,
            },
            // TmdbMatching：唯一/≤N → Classifying；>N 或零结果 → AiParsing；
            //              剧集字段不全（缺 season/episode）→ AwaitingReview（ParseIncomplete 守护，避免归档失败）
            [MediaItemStatus.TmdbMatching] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Classifying,
                MediaItemStatus.AiParsing,
                MediaItemStatus.AwaitingReview,
                MediaItemStatus.Cancelled,
            },
            // AiParsing：必走 TmdbRematching
            [MediaItemStatus.AiParsing] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.TmdbRematching,
                MediaItemStatus.Cancelled,
            },
            // TmdbRematching：唯一/≤N → Classifying；其他 → AwaitingReview
            [MediaItemStatus.TmdbRematching] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Classifying,
                MediaItemStatus.AwaitingReview,
                MediaItemStatus.Cancelled,
            },
            // Classifying：规则/AI 命中 → Archiving；均失败 → AwaitingReview
            [MediaItemStatus.Classifying] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Archiving,
                MediaItemStatus.AwaitingReview,
                MediaItemStatus.Cancelled,
            },
            // AwaitingReview：人工确认 → Archiving；人工忽略 → Ignored；
            //                TMDB 未收录（TmdbZeroResult）每日自动重投 → Queued（重新走全管线，收录后自动归档）
            [MediaItemStatus.AwaitingReview] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Archiving,
                MediaItemStatus.Ignored,
                MediaItemStatus.Queued,
            },
            // Archiving：成功 → Completed；同名冲突跳过 / 忽略规则 → Skipped；
            //            同名冲突且策略为「询问(Ask)」→ AwaitingReview（退回人工裁定是否覆盖，复用 NameCollision 原因）
            [MediaItemStatus.Archiving] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Completed,
                MediaItemStatus.Skipped,
                MediaItemStatus.AwaitingReview,
            },
            // 终态：Completed / Skipped / Ignored 无合法转移
            [MediaItemStatus.Completed] = new HashSet<MediaItemStatus>(),
            [MediaItemStatus.Skipped] = new HashSet<MediaItemStatus>(),
            [MediaItemStatus.Ignored] = new HashSet<MediaItemStatus>(),
            [MediaItemStatus.Cancelled] = new HashSet<MediaItemStatus>(),
            // Failed：只能由 History.Rescan 拉回 Queued
            [MediaItemStatus.Failed] = new HashSet<MediaItemStatus>
            {
                MediaItemStatus.Queued,
            },
        };

    /// <summary>状态机迁移（违反 §6.2 抛 DomainException）</summary>
    /// <remarks>
    /// 任何状态都可通过 MarkFailed(reason) 进入 Failed（不走本方法）；
    /// 自循环（next == 当前 Status）一律视为非法（避免应用层误调）。
    /// </remarks>
    public void Transition(MediaItemStatus next)
    {
        if (next == Status)
            throw new DomainException($"状态机非法转移：{Status} → {next}（自循环不允许）");

        if (!AllowedTransitions.TryGetValue(Status, out IReadOnlySet<MediaItemStatus>? allowed)
            || !allowed.Contains(next))
            throw new DomainException($"状态机非法转移：{Status} → {next}（§6.2 未定义）");

        Status = next;
        if (next == MediaItemStatus.Completed)
            ArchivedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>转入 AwaitingReview 同时记录原因（推荐替代单纯 Transition(AwaitingReview)）</summary>
    /// <remarks>
    /// 内部仍走 Transition 保证状态机合法性校验；reason 必须显式给出（None 视为非法，调用方应明确指定一个业务原因）。
    /// 7 个决策分支调用约定：
    ///   - ProcessFileService AI 失败 → AiLowConfidence
    ///   - ProcessFileService AI 后 TMDB 二次零候选 → TmdbZeroResult
    ///   - ProcessFileService AI 后 TMDB 二次多候选 → TmdbMultiCandidate
    ///   - ProcessFileService TMDB 候选综合得分低于门槛（四维加权择优无法可信取舍） → TmdbMultiCandidate
    ///   - ProcessFileService 分类无命中 → CategoryUnresolved
    ///   - 归档同名冲突且冲突策略为「询问(Ask)」需人工裁定是否覆盖 → NameCollision（ProcessFileService 自动管线 / ReviewService 确认归档冲突分支）
    ///   - ProcessFileService 剧集解析缺 season / episode → ParseIncomplete
    /// </remarks>
    public void MarkAwaitingReview(ReviewReason reason)
    {
        if (reason == Domain.Enums.ReviewReason.None)
            throw new DomainException("MarkAwaitingReview 必须给出业务原因（不允许 None）");
        Transition(MediaItemStatus.AwaitingReview);
        ReviewReason = reason;
    }

    /// <summary>异常路径：任何非终态都可标记失败</summary>
    /// <remarks>
    /// Completed / Skipped / Ignored 已是终态，再 MarkFailed 视为非法（避免覆盖历史结果）。
    /// AttemptCount + LastAttemptAt 由本方法维护，便于 History 展示。
    /// </remarks>
    public void MarkFailed(string reason)
    {
        if (Status is MediaItemStatus.Completed
            or MediaItemStatus.Skipped
            or MediaItemStatus.Ignored
            or MediaItemStatus.Cancelled)
        {
            throw new DomainException($"状态机非法转移：{Status} → Failed（终态不可再失败）");
        }

        Status = MediaItemStatus.Failed;
        ErrorMessage = reason;
        AttemptCount += 1;
        LastAttemptAt = DateTimeOffset.UtcNow;
    }

    /// <summary>启动恢复：把异常关停遗留的「在途」记录重置回 Queued，便于重新入队处理</summary>
    /// <remarks>
    /// 仅供 StartupRecoveryWorker 调用：进程内处理队列（PendingFileQueue）是内存 Channel，进程重启即丢；
    /// 这些「已入队但没跑完」的在途记录不会被任何机制重新拾取（强制扫描只重投 Failed、普通扫描跳过已存在 SourcePath），
    /// 不重置就永久搁浅在 Queued / 中途态。
    /// 允许来源态：Detected / Queued / Parsing / TmdbMatching / AiParsing / TmdbRematching / Classifying；
    /// 已是 Queued 则原地幂等。AwaitingReview（等待人工）与 Archiving（由 RecoverStuckArchiving 单独处理）及 4 个终态一律拒绝。
    /// 直接改 Status 而不走 Transition：这是「中断回退」语义，前向状态机转移表本就不覆盖回退。
    /// </remarks>
    public void RequeueInterrupted()
    {
        if (Status is MediaItemStatus.Detected
            or MediaItemStatus.Queued
            or MediaItemStatus.Parsing
            or MediaItemStatus.TmdbMatching
            or MediaItemStatus.AiParsing
            or MediaItemStatus.TmdbRematching
            or MediaItemStatus.Classifying)
        {
            Status = MediaItemStatus.Queued;
            return;
        }
        throw new DomainException($"RequeueInterrupted 非法来源状态：{Status}（仅在途态可重排回 Queued）");
    }

    /// <summary>人工干预：把记录直接置入 Archiving 以手动重跑归档（详情页「手动移动」）</summary>
    /// <remarks>
    /// 仅供 HistoryService.ManualArchiveAsync 调用：自动归档失败（Failed）、同名冲突跳过（Skipped）
    /// 或滞留人工确认队列（AwaitingReview）时，用户在单媒体详情页直接「手动移动 / 复制」重跑归档步骤。
    /// 与 RequeueInterrupted 同属「人工干预 / 回退」语义——直接置 Status=Archiving 而不走 Transition（前向状态机
    /// 转移表本就不覆盖 Failed/Skipped→Archiving 这类人工回拨）。置位后由调用方调 IArchiveService 实移，
    /// 成功 Transition 到 Completed/Skipped、失败 MarkFailed（此刻 Status=Archiving 非终态，MarkFailed 合法）。
    /// 要求 TmdbId / TmdbMediaType / CategoryId / ParsedInfo 齐全（否则 ArchiveService 会抛 BusinessException）。
    /// Completed（已归档完成）/ Ignored（已人工忽略）拒绝：不应再手动重移。
    /// </remarks>
    public void BeginManualArchive()
    {
        if (Status is MediaItemStatus.Failed
            or MediaItemStatus.Skipped
            or MediaItemStatus.AwaitingReview)
        {
            Status = MediaItemStatus.Archiving;
            return;
        }
        throw new DomainException($"BeginManualArchive 非法来源状态：{Status}（仅 Failed / Skipped / AwaitingReview 可手动归档）");
    }

    /// <summary>人工干预：撤销已完成的归档，把记录从 Completed 回退到 Skipped（未入库）</summary>
    /// <remarks>
    /// 仅供 HistoryService.UndoArchiveAsync 调用：用户在历史 / 详情页「撤销归档」，调用方先按文件系统状态
    /// 反向 move 文件回源位置（或删归档副本），再调本方法回退状态。
    /// 选 Skipped 而非 Queued 的本因：① Skipped 语义即「未归档入库」，撤销后文件已不在媒体库，吻合；
    /// ② Skipped 是终态，不会被 StartupRecoveryWorker 重排 / 定时全量扫描自动重跑——避免按原规则立刻重新归档回
    ///    同一错误位置使撤销形同虚设；记录仍在（挡住按 SourcePath 的重新检测）；
    /// ③ 用户随后可在改正规则后主动「重新处理」(Reprocess) 或「手动移动」(BeginManualArchive，Skipped 为其合法来源态)。
    /// 与 RequeueInterrupted / BeginManualArchive 同属「回退语义」——直接置 Status 绕过前向状态机；
    /// 清 TargetPath / ArchivedAt（已不在该位置）与 FileMissing / FileCheckedAt（旧目标存在性结论已失效）。
    /// 仅 Completed 可撤销（其它状态归档尚未完成或本就未归档，无撤销语义）。
    /// </remarks>
    public void UndoArchive()
    {
        if (Status != MediaItemStatus.Completed)
            throw new DomainException($"UndoArchive 非法来源状态：{Status}（仅 Completed 已归档完成可撤销）");
        Status = MediaItemStatus.Skipped;
        TargetPath = null;
        ArchivedAt = null;
        FileMissing = false;
        FileCheckedAt = null;
    }

    /// <summary>人工干预：把已确认归档的记录退回人工确认队列，供修正填错的资料后重新确认</summary>
    /// <remarks>
    /// 仅供 HistoryService.ReopenForReviewAsync 调用：用户在历史 / 详情页发现确认时填错 TMDB / 分类 / 季集，
    /// 「退回重改」一键把记录退回 AwaitingReview——调用方先把已完成归档文件反向 move 回源位
    /// （Skipped 文件本就在源位、不动），再调本方法回退状态；记录重新出现在人工确认队列，可改资料后再确认归档。
    /// 与 UndoArchive 的区别：UndoArchive 回 Skipped（撤销入库、不再自动流转）；本方法回 AwaitingReview（明确要重新人工确认）。
    /// AwaitingReview 在等人工、不会被自动流程推进，不存在「按原错误规则立刻重新归档回同一错误位置」之虞，故可安全回退到此态。
    /// 与 RequeueInterrupted / BeginManualArchive / UndoArchive 同属「回退语义」——直接置 Status 绕过前向状态机。
    /// 置 ReviewReason=ManualReopen（确认页提示来由）；清 TargetPath / ArchivedAt / FileMissing / FileCheckedAt（已不在归档位，旧存在性结论失效）；
    /// 保留 TmdbId / CategoryId / ParsedInfo / TmdbCandidatesJson（让确认页预填上次填错的值，在其基础上改而非从零填）。
    /// 允许来源态：Completed（已归档完成）/ Skipped（确认时同名冲突 / 撤销归档后）——均为人工 confirm 的落点。
    /// </remarks>
    public void ReopenForReview()
    {
        if (Status is not (MediaItemStatus.Completed or MediaItemStatus.Skipped))
            throw new DomainException($"ReopenForReview 非法来源状态：{Status}（仅 Completed / Skipped 可退回人工确认）");
        Status = MediaItemStatus.AwaitingReview;
        ReviewReason = Domain.Enums.ReviewReason.ManualReopen;
        TargetPath = null;
        ArchivedAt = null;
        FileMissing = false;
        FileCheckedAt = null;
    }

    /// <summary>查询：是否处于终态（5 个：Completed / Skipped / Ignored / Cancelled / Failed）</summary>
    public bool IsTerminal() => Status is
        MediaItemStatus.Completed
        or MediaItemStatus.Skipped
        or MediaItemStatus.Ignored
        or MediaItemStatus.Cancelled
        or MediaItemStatus.Failed;

    /// <summary>自动流程采纳 TMDB 候选 + AI/规则解析结果（5 字段一次性写入）</summary>
    /// <remarks>
    /// 由 ProcessFileService 在 Classifying 前调用；JSON 序列化交由 ParsedInfo 值对象完成，
    /// 避免 Service 层拼裸 JSON。终态调用拒绝（保护已归档/已忽略记录）。
    /// </remarks>
    public void ApplyTmdbMatch(int tmdbId, string tmdbMediaType, ParseSource parseSource, double? confidence, ParsedInfo parsedInfo)
    {
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改 TMDB 匹配结果");
        TmdbId = tmdbId;
        TmdbMediaType = tmdbMediaType;
        ParseSource = parseSource;
        Confidence = confidence;
        ParsedInfo = parsedInfo.ToJson();
    }

    /// <summary>标记本次处理动用了 AI 解析（幂等，不受终态限制）</summary>
    /// <remarks>
    /// 在 AI 升级链真正发起调用的时刻置位（早于结果采纳），即使 AI 失败转人工也保留——
    /// 「AI 参与」记录的是过程事实而非结果来源（结果来源看 ParseSource）。
    /// </remarks>
    public void MarkAiInvolved() => AiInvolved = true;

    /// <summary>人工 Review 确认匹配（TmdbId + MediaType + CategoryId + ParsedInfo 一次写入）</summary>
    /// <remarks>
    /// 由 ReviewService.ConfirmAsync 调用；ParseSource 保持原值（自动流程留下的 Rule/Ai/Hybrid 痕迹不被人工覆盖）。
    /// </remarks>
    public void ApplyManualMatch(int tmdbId, string tmdbMediaType, long categoryId, ParsedInfo parsedInfo)
    {
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改 TMDB 匹配结果");
        TmdbId = tmdbId;
        TmdbMediaType = tmdbMediaType;
        CategoryId = categoryId;
        ParsedInfo = parsedInfo.ToJson();
    }

    /// <summary>Review 用户重绑 TMDB（不动 CategoryId，仅写 TmdbId / MediaType / ParsedInfo）</summary>
    public void RebindTmdb(int tmdbId, string tmdbMediaType, ParsedInfo parsedInfo)
    {
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改 TMDB 匹配结果");
        TmdbId = tmdbId;
        TmdbMediaType = tmdbMediaType;
        ParsedInfo = parsedInfo.ToJson();
    }

    /// <summary>写入审核候选快照（转 AwaitingReview 前由 ProcessFileService 调用）</summary>
    /// <remarks>候选结构在 Persistence 层定义并序列化，Domain 仅持有字符串保持零依赖；终态拒绝。</remarks>
    public void SetTmdbCandidates(string? candidatesJson)
    {
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改 TMDB 候选快照");
        TmdbCandidatesJson = candidatesJson;
    }

    /// <summary>自动分类决策后写 CategoryId（不改状态，调用方负责 Transition）</summary>
    public void AssignCategory(long categoryId)
    {
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改分类");
        CategoryId = categoryId;
    }

    /// <summary>归档完成后写 TargetPath（状态由调用方 Transition 到 Completed/Skipped）</summary>
    public void SetArchiveResult(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new DomainException("TargetPath 不能为空");
        TargetPath = targetPath;
    }

    /// <summary>写入内容采样哈希（内容去重；由 ProcessFileService 在 hash 尚未计算时调用）</summary>
    /// <remarks>
    /// 值为采样 SHA256（头部 + 尾部 + 文件大小，详见 IFileHasher 实现）的十六进制字符串；
    /// 内容身份不随状态变化，但仍守护终态——避免已归档 / 已忽略记录被改写哈希。
    /// </remarks>
    public void SetFileHash(string fileHash)
    {
        if (string.IsNullOrWhiteSpace(fileHash))
            throw new DomainException("FileHash 不能为空");
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改 FileHash");
        FileHash = fileHash;
    }

    /// <summary>记录失败摘要但不改状态（用于 AI 兜底失败后转 AwaitingReview 前留痕迹）</summary>
    public void RecordError(string message)
    {
        ErrorMessage = message;
    }

    /// <summary>清除 ErrorMessage（History.Rescan 重置回 Queued 前调用）</summary>
    public void ClearError()
    {
        ErrorMessage = null;
    }

    /// <summary>记录文件存在性检查结果（仅写 FileMissing + FileCheckedAt，不动状态机）</summary>
    /// <remarks>由整库存在性扫描调用，任意状态可调（含终态）。</remarks>
    public void MarkFileChecked(bool missing, DateTimeOffset at)
    {
        FileMissing = missing;
        FileCheckedAt = at;
    }

    /// <summary>写入音频探测结果（归档前由 ProcessFileService 调用）</summary>
    /// <remarks>
    /// AudioCodecs 为音轨编解码器名逗号分隔快照（如 "av3a,aac"）；HasIncompatibleAudio 标记是否含 av3a 等不兼容轨。
    /// 技术元数据可刷新，但仍守护终态——避免已归档 / 已忽略记录被改写（归档中 Archiving 非终态，可写）。
    /// </remarks>
    public void SetAudioProbe(string? audioCodecs, bool hasIncompatibleAudio)
    {
        if (IsTerminal())
            throw new DomainException($"终态({Status})不允许再修改音频探测结果");
        AudioCodecs = audioCodecs;
        HasIncompatibleAudio = hasIncompatibleAudio;
    }

    /// <summary>事后刷新音频探测结果（存量批量重扫用，任意状态可调含终态）</summary>
    /// <remarks>
    /// 与 SetAudioProbe（归档前写、守护终态）区分：已归档(Completed 终态)文件的音轨探测属事后技术补标，
    /// 不改状态机、不动业务字段，故仿 MarkFileChecked 不设终态守护。存量扫描正是要写 Completed 记录。
    /// </remarks>
    public void RefreshAudioProbe(string? audioCodecs, bool hasIncompatibleAudio)
    {
        AudioCodecs = audioCodecs;
        HasIncompatibleAudio = hasIncompatibleAudio;
    }

    /// <summary>追加一条处理时间线步骤（由 ProcessFileService 在每次 Transition 之后调用）</summary>
    /// <remarks>
    /// 按红线「充血聚合」要求，ProcessStep 是 MediaItem 聚合内子实体，不允许应用层直接 new ProcessStep 后入库；
    /// 必须经此方法追加，EF Core 走 navigation collection 与 MediaItem 同次 SaveChanges 一并持久化。
    /// Detail 由调用方按 Stage 序列化好的 JSON 字符串传入（前端按 Stage 渲染）。
    /// </remarks>
    public void AppendStep(MediaItemStatus stage, DateTimeOffset startedAt, long durMs, string? detail = null)
    {
        if (durMs < 0)
            throw new DomainException($"AppendStep 非法 durMs={durMs}（必须 ≥ 0）");
        _steps.Add(new ProcessStep(Id, stage, startedAt, durMs, detail));
    }

    /// <summary>测试 / EF Core HasData 种子数据用 fixture 工厂（一次性指定全字段，跳过状态机校验）</summary>
    /// <remarks>
    /// internal 访问，仅 Persistence + 4 个 Tests 程序集（见 Domain.csproj 的 InternalsVisibleTo）可见。
    /// 生产业务流（Application / Host / External / Platform）禁止调用，必须走 CreateDetected + 聚合方法。
    /// </remarks>
    internal static MediaItem CreateFixture(
        string sourcePath,
        string fileName,
        long fileSize,
        MediaItemStatus status = MediaItemStatus.Detected,
        int? tmdbId = null,
        string? tmdbMediaType = null,
        ParseSource? parseSource = null,
        double? confidence = null,
        string? parsedInfo = null,
        long? categoryId = null,
        string? targetPath = null,
        string? errorMessage = null,
        string? fileHash = null,
        ReviewReason? reviewReason = null,
        string? tmdbCandidatesJson = null,
        string? audioCodecs = null,
        bool hasIncompatibleAudio = false)
    {
        return new MediaItem
        {
            SourcePath = sourcePath,
            FileName = fileName,
            FileSize = fileSize,
            Status = status,
            TmdbId = tmdbId,
            TmdbMediaType = tmdbMediaType,
            ParseSource = parseSource,
            Confidence = confidence,
            ParsedInfo = parsedInfo,
            CategoryId = categoryId,
            TargetPath = targetPath,
            ErrorMessage = errorMessage,
            FileHash = fileHash,
            ReviewReason = reviewReason,
            TmdbCandidatesJson = tmdbCandidatesJson,
            AudioCodecs = audioCodecs,
            HasIncompatibleAudio = hasIncompatibleAudio,
        };
    }
}
