using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.MediaItems;

namespace PersonalMediaManager.Application.Services.Archive;

/// <summary>归档服务契约（D7.4 实现）— 文件操作 + 元数据生成 + 出 Outbox 事件</summary>
/// <remarks>
/// 输入：已 Classifying → Archiving 的 MediaItem（含 TmdbId / CategoryId / ParsedInfo）；
/// 输出：TargetPath + Outcome（Completed / ConflictSkipped）。
/// 实现规则：
///   1. PlexNamingConventions 算目标路径（电影 / 剧集 / 特别篇 / 多版本）
///   2. PathSafetyGuard 校验在分类根目录下
///   3. 同名冲突 → 按 Archive_ConflictPolicy 决策（Skip→ConflictSkipped / Overwrite 升级替换 / KeepBoth 多版本 / Ask→ConflictPending 待人工裁定）
///   4. IFileMover 移动文件 + SubtitleRenamer 同步字幕
///   5. MetadataFinalizer 写 nfo + poster + fanart
///   6. 写入 Webhook_Delivery(media.archived) + EnqueueAsync 触发 OutboxWorker
/// 异常策略以「视频是否已落地」为界：落地前（命名 / 路径穿越 / 冲突 / 磁盘 / 移视频）失败 → 抛异常，
/// 由 ProcessFileService catch 后 MarkFailed（源未动，可 Rescan 重试）；落地后 nfo / Webhook 失败 →
/// 不抛，降级为 ArchiveResult.Warnings（Outcome 仍 Completed），避免源已删却整条判 Failed 不可恢复。
/// </remarks>
public interface IArchiveService
{
    /// <summary>归档（默认 Move：自动管线 / 人工确认走此重载）</summary>
    Task<ArchiveResult> ArchiveAsync(MediaItem item, CancellationToken ct = default);

    /// <summary>归档并显式指定文件操作（Move / Copy）— 详情页「手动移动 / 复制」用</summary>
    /// <remarks>Copy 保留源文件（NAS / 网盘场景）；其余流程（命名 / 冲突 / 字幕 / nfo / 海报 / Webhook）与 Move 完全一致。</remarks>
    Task<ArchiveResult> ArchiveAsync(MediaItem item, ArchiveOperation operation, CancellationToken ct = default);

    /// <summary>归档并指定操作 + 音频重混计划（主管线 av3a 等不兼容轨处理用）</summary>
    /// <remarks>remuxPlan 非 null 时第 6 步用 ffmpeg 流复制丢不兼容音轨输出到目标（就近目标盘），替代纯文件移动；null 时与普通归档一致。</remarks>
    Task<ArchiveResult> ArchiveAsync(MediaItem item, ArchiveOperation operation, AudioRemuxPlan? remuxPlan, CancellationToken ct = default);

    /// <summary>归档并显式指定同名冲突处理（覆盖系统设置 Archive_ConflictPolicy）— 审核页人工裁定「覆盖」时用</summary>
    /// <remarks>resolution=ForceOverwrite 时无条件覆盖目标已存在文件（不比新旧大小，删旧 + 清旧伴生字幕/nfo + 失效旧记录）；FollowPolicy 时与普通归档一致（读系统策略）。</remarks>
    Task<ArchiveResult> ArchiveAsync(MediaItem item, ArchiveOperation operation, ArchiveConflictResolution resolution, CancellationToken ct = default);
}

/// <summary>归档文件操作模式</summary>
public enum ArchiveOperation
{
    /// <summary>移动（同卷 rename / 跨卷 Copy+Delete，删源）</summary>
    Move = 1,
    /// <summary>复制（保留源文件，NAS / 网盘场景）</summary>
    Copy = 2,
}

/// <summary>归档同名冲突处理的「单次调用覆盖」（优先于系统设置 Archive_ConflictPolicy）</summary>
/// <remarks>仅审核页人工裁定覆盖等需绕过系统策略的场景使用；默认 FollowPolicy 即读系统设置。</remarks>
public enum ArchiveConflictResolution
{
    /// <summary>遵循系统设置 Archive_ConflictPolicy（默认）</summary>
    FollowPolicy = 0,
    /// <summary>无条件覆盖目标已存在文件（人工裁定，不比新旧大小）</summary>
    ForceOverwrite = 1,
}

/// <param name="TargetPath">归档后目标文件绝对路径（即使 ConflictSkipped / ConflictPending 也返回预期路径供日志展示）</param>
/// <param name="Outcome">Completed=视频已落地（元数据可能待补，见 Warnings）；ConflictSkipped=目标已存在同名文件已跳过；ConflictPending=目标已存在同名文件、按「询问」策略待人工裁定覆盖（均未做任何文件操作）</param>
/// <param name="Warnings">视频已落地、但 nfo / Webhook 等落地后步骤失败的「待补元数据」警告（null / 空 = 全部成功）；不改变 Outcome=Completed</param>
public sealed record ArchiveResult(string TargetPath, ArchiveOutcome Outcome, IReadOnlyList<string>? Warnings = null);

public enum ArchiveOutcome
{
    /// <summary>视频已落地完成（nfo / Webhook 可能失败降级为 Warnings，仍属完成）</summary>
    Completed = 1,
    /// <summary>目标已存在同名文件，未做任何文件操作（跳过 / 升级替换不满足）</summary>
    ConflictSkipped = 2,
    /// <summary>目标已存在同名文件，按「询问(Ask)」策略待人工裁定是否覆盖（未做任何文件操作，编排层应转入待确认队列）</summary>
    ConflictPending = 3,
}
