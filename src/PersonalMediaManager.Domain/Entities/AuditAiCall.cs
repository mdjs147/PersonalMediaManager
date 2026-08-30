using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>AI 调用统计（Audit_AiCall）</summary>
/// <remarks>
/// 由 AiProviderHealthTracker（D3.4）滚动 10 分钟窗口扫描失败次数 ≥5 → 写 Parse_AiProvider.DisabledUntil = now+30min。
/// 复合索引 (ProviderId, Success, Timestamp) 覆盖失败率统计；OnDelete: Provider→CASCADE / MediaItem→SET NULL。
///
/// 监控增强（0.13.0）：除「成功 / 延迟 / 错误」外补采更多诊断维度——
/// 模型名 <see cref="Model"/>、token 用量 <see cref="PromptTokens"/>/<see cref="CompletionTokens"/>（成本视角）、
/// 结果置信度 <see cref="Confidence"/>、HTTP 状态码 <see cref="HttpStatus"/>、升级链关联
/// (<see cref="ChainId"/> + <see cref="AttemptLevel"/> + <see cref="IsPrimary"/>) 与请求/响应原文
/// (<see cref="RequestText"/>/<see cref="ResponseText"/>)。新列均可空/带默认，对历史行向后兼容（旧行为 NULL/0）。
/// 增长由 AiCallRetentionJob 按天 + 单 provider 行数上限双闸清理（System_Setting 可调），防原文累积膨胀。
/// </remarks>
public sealed class AuditAiCall : Entity
{
    public long ProviderId { get; set; }

    public long? MediaItemId { get; set; }

    public bool Success { get; set; }

    public int LatencyMs { get; set; }

    /// <summary>Timeout / Http4xx / Http5xx / Parse / Network / RateLimit / LowConfidence / ConfigError</summary>
    public string? ErrorType { get; set; }

    public string? ErrorDetail { get; set; }

    /// <summary>调用时使用的模型名（provider 后续改模型后，历史行仍可按调用当时的模型归因）</summary>
    public string? Model { get; set; }

    /// <summary>提示词消耗 token（厂商响应 usage 返回则填，否则 null）</summary>
    public int? PromptTokens { get; set; }

    /// <summary>补全消耗 token（厂商响应 usage 返回则填，否则 null）</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>本次结果的 AI 自评置信度（含「低置信软失败」行）；无解析结果（接口故障）时 null</summary>
    public double? Confidence { get; set; }

    /// <summary>HTTP 状态码（成功记 200，失败记真实状态码；网络/瞬时错误无状态码时 null）</summary>
    public int? HttpStatus { get; set; }

    /// <summary>同一文件单次解析升级链的关联 Id（同链多级共享）；用于还原「主→备→更高级」轨迹</summary>
    public string? ChainId { get; set; }

    /// <summary>本行在升级链中的级序（1 起；0 = 历史行/未知）</summary>
    public int AttemptLevel { get; set; }

    /// <summary>调用时该 provider 是否为主提供商</summary>
    public bool IsPrimary { get; set; }

    /// <summary>请求原文（仅文件相关的 user 提示词，不含恒定 system 提示，省体积）</summary>
    public string? RequestText { get; set; }

    /// <summary>响应原文（成功为助手原始文本；失败为错误响应体，可空；写入端按安全阀截断）</summary>
    public string? ResponseText { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}
