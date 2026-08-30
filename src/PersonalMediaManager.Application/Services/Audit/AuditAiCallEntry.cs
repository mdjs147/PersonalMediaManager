namespace PersonalMediaManager.Application.Services.Audit;

/// <summary>Audit_AiCall 单行写入入参</summary>
/// <remarks>
/// 监控增强后采集维度变多，聚成记录避免十余个位置参数列表易错；可选字段缺省即历史口径（仅成功/延迟/错误）。
/// RequestText / ResponseText 原文由写入端按安全阀截断（防异常巨包），调用方传完整原文即可。
/// </remarks>
public sealed record AuditAiCallEntry(
    long ProviderId,
    long? MediaItemId,
    bool Success,
    int LatencyMs,
    string? ErrorType = null,
    string? ErrorDetail = null,
    string? Model = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    double? Confidence = null,
    int? HttpStatus = null,
    string? ChainId = null,
    int AttemptLevel = 0,
    bool IsPrimary = false,
    string? RequestText = null,
    string? ResponseText = null);
