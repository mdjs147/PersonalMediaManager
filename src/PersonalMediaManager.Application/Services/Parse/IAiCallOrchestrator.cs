using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>AI 兜底调用编排（D3.3）— 把 AiCallChain 聚合、Resolver、IAiParser、Audit 写入串成一条多级升级链</summary>
/// <remarks>
/// 链路规则（需求文档 §3.3.3 + 多级升级扩展）：
/// - 升级序列 = Resolver 输出的全部可用 provider（主 → 各备用，按 Priority 升序）；级数上限 = provider 数（钳到 AiCallChain.MaxHardCap）
/// - 每级失败都升级到下一级，触发升级的条件：
///     · 接口异常（瞬时重试耗尽 / 4xx / 5xx / 逻辑错误）
///     · 限流 / 配额（429 → AiProviderRateLimitException）：本级达上限，立即升级到更高级 AI
///     · 结果不满意（置信度 &lt; 本级 ConfidenceThreshold）：无条件升级到下一级（落实「Ollama 反馈不满意时升级」）
/// - 同 provider 瞬时错误允许内部 1 次重试（500ms 退避），不计入级数额度
/// - 任一级成功且结果可接受 → 终止；全部级别耗尽 → 总失败 → 调用方 SendToReview
/// - 每次 provider 调用结束（成功 / 失败 / 低置信）都写一条 Audit_AiCall 行，供 D3.4 健康追踪
///   （低置信记 errorType=LowConfidence，但不计入自动禁用——provider 本身健康，只是结果不够好）
///
/// 调用方约定：ProcessFileService（D7.1）拿到 Outcome 后：
///   - Outcome.Success=true → 用 Result 进入二次 TMDB 查询
///   - Outcome.Success=false → MediaItem.Transition(AwaitingReview)
///   - Outcome.Attempts → 落 Process_Step.Detail 供解析详情页展示完整升级轨迹
/// </remarks>
public interface IAiCallOrchestrator
{
    Task<AiCallOutcome> ExecuteAsync(AiParseRequest request, long? mediaItemId, CancellationToken ct = default);
}

/// <summary>编排结果</summary>
/// <param name="Success">是否拿到可用 AI 解析结果</param>
/// <param name="Result">成功时的解析结果（Success=true 时非空）</param>
/// <param name="WinningProviderId">成功时使用的 provider Id（写 MediaItem.ParseSource 用）</param>
/// <param name="ProvidersAttempted">实际消耗的升级级数（0~HardCap）；0 表示无可用 provider（Resolver 返回空）</param>
/// <param name="FailureSummary">总失败时的人类可读摘要（写 MediaItem.ErrorMessage）</param>
/// <param name="Attempts">逐级升级轨迹（按尝试顺序）；供解析详情页展示「试过哪几级、各级为何升级、最终哪级成功」</param>
public sealed record AiCallOutcome(
    bool Success,
    AiParseResult? Result,
    long? WinningProviderId,
    int ProvidersAttempted,
    string? FailureSummary,
    IReadOnlyList<AiCallAttempt>? Attempts = null);

/// <summary>单级 AI 调用的轨迹记录（一条对应升级链里的一级）</summary>
/// <param name="Level">第几级（1-based，1=主提供商/Ollama，2=升级到的下一级，以此类推）</param>
/// <param name="ProviderId">provider 主键</param>
/// <param name="ProviderName">provider 展示名</param>
/// <param name="ProviderType">provider 类型</param>
/// <param name="IsPrimary">是否主提供商</param>
/// <param name="Success">本级是否产出可接受结果（true=终结链路；false=升级到下一级）</param>
/// <param name="Confidence">本级返回的置信度（无结果时 null）</param>
/// <param name="ErrorType">升级原因分类：Transient / RateLimit / Http4xx / Http5xx / Logical / LowConfidence / ConfigError；成功为 null</param>
/// <param name="ErrorDetail">升级原因详情（人类可读）</param>
/// <param name="LatencyMs">本级耗时（毫秒）</param>
public sealed record AiCallAttempt(
    int Level,
    long ProviderId,
    string ProviderName,
    AiProviderType ProviderType,
    bool IsPrimary,
    bool Success,
    double? Confidence,
    string? ErrorType,
    string? ErrorDetail,
    int LatencyMs);
