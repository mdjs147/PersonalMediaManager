using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Parse;

/// <summary>AI 提供商响应 DTO（不暴露 ApiKey 密文）</summary>
/// <remarks>
/// Calls7d / SuccessRate / AvgLatency 为近 7 天 Audit_AiCall 聚合概览（列表卡片用）：
/// 无调用时 Calls7d=0、SuccessRate / AvgLatency 为 null（前端显示「—」）；
/// 平均延迟仅计成功调用，与详情页 stats 接口同口径。CRUD 单条返回时这三项取默认值（0 / null）。
/// ConfidenceThreshold：满意度阈值（升级触发线），null = 回退全局 Parse.AiConfidenceThreshold。
/// CostTier：成本档位（Free 享节流豁免 + 高阈值默认）；StructuredJson：是否支持结构化 JSON 输出。
/// EffectiveThreshold / ThresholdSource：服务端按「自定义 ConfidenceThreshold 优先，否则回退全局 + 免费档高阈值默认」
/// 算出的<b>实际生效阈值</b>与其<b>来源</b>（"custom" = 用本 provider 自定义值；"global" = 回退全局/档位默认），供前端直接展示无需重算。
/// 套餐配额 6 字段：QuotaCallLimit / QuotaTokenLimit / QuotaExpiresAt 为配置（null = 不限），
/// QuotaUsedCalls / QuotaUsedTokens 为已累计用量（成功失败都计），QuotaExceededAt 非空即用量超限自动禁用
/// （与 Enabled / DisabledUntil 三态分离；到期禁用无标记，前端按 QuotaExpiresAt 与当前时间自行展示）。
/// </remarks>
public sealed record ParseAiProviderResponse(
    long Id,
    string Name,
    AiProviderType Type,
    AiCostTier CostTier,
    bool StructuredJson,
    string BaseUrl,
    bool HasApiKey,
    string Model,
    bool IsPrimary,
    int Priority,
    double? ConfidenceThreshold,
    double? EffectiveThreshold,
    string? ThresholdSource,
    bool Enabled,
    DateTimeOffset? DisabledUntil,
    int? QuotaCallLimit,
    long? QuotaTokenLimit,
    DateTimeOffset? QuotaExpiresAt,
    long QuotaUsedCalls,
    long QuotaUsedTokens,
    DateTimeOffset? QuotaExceededAt,
    // 周期滚动额度：粒度（None=不启用）+ 时区 + 周期上限（null=不限）+ 周期已用量 + 本周期重置时刻（UTC）
    AiQuotaPeriod QuotaPeriod,
    string? QuotaPeriodTimeZone,
    int? QuotaPeriodCallLimit,
    long? QuotaPeriodTokenLimit,
    long QuotaPeriodUsedCalls,
    long QuotaPeriodUsedTokens,
    DateTimeOffset? QuotaPeriodResetAt,
    int? RpmLimit,
    int TimeoutSeconds,
    string? ExtraOptions,
    bool UseProxy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Calls7d = 0,
    int? SuccessRate = null,
    int? AvgLatency = null);

public sealed record CreateParseAiProviderRequest(
    [RequiredNotBlank(ErrorMessage = "Name 不能为空")]
    [MaxLength(64, ErrorMessage = "Name 长度不能超过 64 字符")]
    string Name,
    AiProviderType Type,
    [RequiredNotBlank(ErrorMessage = "BaseUrl 不能为空")]
    [MaxLength(500, ErrorMessage = "BaseUrl 长度不能超过 500 字符")]
    [HttpUrl(ErrorMessage = "BaseUrl 必须是合法的 http / https URL")]
    string BaseUrl,
    string? ApiKey,
    [RequiredNotBlank(ErrorMessage = "Model 不能为空")]
    [MaxLength(100, ErrorMessage = "Model 长度不能超过 100 字符")]
    string Model,
    bool IsPrimary = false,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority = 100,
    // null 留空合法（回退全局阈值）；[Range] 对 null 自动放行
    [Range(0.0, 1.0, ErrorMessage = "满意度阈值必须在 [0, 1] 范围内（留空则回退全局阈值）")]
    double? ConfidenceThreshold = null,
    bool Enabled = true,
    [Range(1, 600, ErrorMessage = "TimeoutSeconds 必须在 [1, 600] 范围内")]
    int TimeoutSeconds = 30,
    [MaxLength(2000, ErrorMessage = "ExtraOptions 长度不能超过 2000 字符")]
    string? ExtraOptions = null,
    bool UseProxy = false,
    AiCostTier CostTier = AiCostTier.Paid,
    bool StructuredJson = true,
    // 套餐配额三项均可空（null = 不限）；[Range] 对 null 自动放行
    [Range(1, int.MaxValue, ErrorMessage = "QuotaCallLimit 必须 ≥ 1（留空 = 不限）")]
    int? QuotaCallLimit = null,
    [Range(1d, double.MaxValue, ErrorMessage = "QuotaTokenLimit 必须 ≥ 1（留空 = 不限）")]
    long? QuotaTokenLimit = null,
    DateTimeOffset? QuotaExpiresAt = null,
    // 周期滚动额度（可选，正交于上方终身配额）：粒度默认 None（不启用）；启用后按时区到自然边界自动重置计数与恢复
    AiQuotaPeriod QuotaPeriod = AiQuotaPeriod.None,
    [MaxLength(64, ErrorMessage = "QuotaPeriodTimeZone 长度不能超过 64 字符")]
    string? QuotaPeriodTimeZone = null,
    [Range(1, int.MaxValue, ErrorMessage = "QuotaPeriodCallLimit 必须 ≥ 1（留空 = 不限）")]
    int? QuotaPeriodCallLimit = null,
    [Range(1d, double.MaxValue, ErrorMessage = "QuotaPeriodTokenLimit 必须 ≥ 1（留空 = 不限）")]
    long? QuotaPeriodTokenLimit = null,
    // RPM 每分钟请求上限（滑动 60 秒；达标跳过升级）；null=不限流
    [Range(1, int.MaxValue, ErrorMessage = "RpmLimit 必须 ≥ 1（留空 = 不限流）")]
    int? RpmLimit = null);

public sealed record UpdateParseAiProviderRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "Name 不能为空")]
    [MaxLength(64, ErrorMessage = "Name 长度不能超过 64 字符")]
    string Name,
    AiProviderType Type,
    [RequiredNotBlank(ErrorMessage = "BaseUrl 不能为空")]
    [MaxLength(500, ErrorMessage = "BaseUrl 长度不能超过 500 字符")]
    [HttpUrl(ErrorMessage = "BaseUrl 必须是合法的 http / https URL")]
    string BaseUrl,
    string? ApiKey,
    [RequiredNotBlank(ErrorMessage = "Model 不能为空")]
    [MaxLength(100, ErrorMessage = "Model 长度不能超过 100 字符")]
    string Model,
    bool IsPrimary,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority,
    bool Enabled,
    [Range(1, 600, ErrorMessage = "TimeoutSeconds 必须在 [1, 600] 范围内")]
    int TimeoutSeconds,
    [MaxLength(2000, ErrorMessage = "ExtraOptions 长度不能超过 2000 字符")]
    string? ExtraOptions,
    bool UseProxy = false,
    // null 留空合法（回退全局阈值）；[Range] 对 null 自动放行
    [Range(0.0, 1.0, ErrorMessage = "满意度阈值必须在 [0, 1] 范围内（留空则回退全局阈值）")]
    double? ConfidenceThreshold = null,
    AiCostTier CostTier = AiCostTier.Paid,
    bool StructuredJson = true,
    // 套餐配额三项为全量语义（null = 清除该限额）；[Range] 对 null 自动放行
    [Range(1, int.MaxValue, ErrorMessage = "QuotaCallLimit 必须 ≥ 1（留空 = 不限）")]
    int? QuotaCallLimit = null,
    [Range(1d, double.MaxValue, ErrorMessage = "QuotaTokenLimit 必须 ≥ 1（留空 = 不限）")]
    long? QuotaTokenLimit = null,
    DateTimeOffset? QuotaExpiresAt = null,
    // 周期滚动额度（全量语义，正交于上方终身配额）：改粒度（含启用/停用/切换）时服务端重置本周期计量
    AiQuotaPeriod QuotaPeriod = AiQuotaPeriod.None,
    [MaxLength(64, ErrorMessage = "QuotaPeriodTimeZone 长度不能超过 64 字符")]
    string? QuotaPeriodTimeZone = null,
    [Range(1, int.MaxValue, ErrorMessage = "QuotaPeriodCallLimit 必须 ≥ 1（留空 = 不限）")]
    int? QuotaPeriodCallLimit = null,
    [Range(1d, double.MaxValue, ErrorMessage = "QuotaPeriodTokenLimit 必须 ≥ 1（留空 = 不限）")]
    long? QuotaPeriodTokenLimit = null,
    // RPM 每分钟请求上限（滑动 60 秒；达标跳过升级）；null=不限流
    [Range(1, int.MaxValue, ErrorMessage = "RpmLimit 必须 ≥ 1（留空 = 不限流）")]
    int? RpmLimit = null);

public sealed record DeleteParseAiProviderRequest(long Id);

public sealed record TestParseAiProviderRequest(long Id);

public sealed record TestParseAiProviderResponse(
    bool Success,
    int? HttpStatus,
    double ElapsedMilliseconds,
    string? ErrorMessage,
    string? ResponseSnippet);

public sealed record EnableParseAiProviderRequest(long Id);

/// <summary>重置套餐用量请求（开始新套餐周期 / 续购时用）</summary>
public sealed record ResetQuotaParseAiProviderRequest(long Id);

/// <summary>AI 提供商调用统计（按时间窗聚合 Audit_AiCall）</summary>
/// <remarks>
/// WindowHours：聚合窗口（默认 24h，可选 1/24/168）。
/// HourlyBuckets：长度 = WindowHours，按 (now - Timestamp).Hours 桶倒序对应到正向时间轴；
/// 取整成 0..N-1，最早→最新。空桶 total=0/failed=0。
/// P95LatencyMs：仅 Success=true 的调用计入；Failed 不参与延迟统计（错误延迟无业务意义）。
/// TotalPromptTokens / TotalCompletionTokens：窗口内 token 用量合计（成本视角；厂商未返回 usage 的调用计 0）。
/// AvgConfidence：有置信度的调用（含低置信软失败行）的均值；窗口内无任何置信度记录时 null。
/// ErrorBreakdown：按 ErrorType GROUP BY 失败调用；null 归入 'Unknown'（含 LowConfidence 升级标记）。
/// ModelBreakdown：按 Model GROUP BY（Model 为空的历史行不计入）：每模型调用数 + token 合计，倒序。
/// RecentCalls：按 Timestamp 倒序取前 N=12 条（含置信度/错误详情/token/模型/链路，激活监控页明细列）。
/// </remarks>
public sealed record AiProviderStatsResponse(
    long ProviderId,
    int WindowHours,
    int TotalCalls,
    int SuccessCount,
    int FailedCount,
    double AvgLatencyMs,
    int P95LatencyMs,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    double? AvgConfidence,
    IReadOnlyList<AiCallHourlyBucket> HourlyBuckets,
    IReadOnlyList<AiCallLatencyBucket> LatencyHistogram,
    IReadOnlyList<AiCallErrorBucket> ErrorBreakdown,
    IReadOnlyList<AiCallModelBucket> ModelBreakdown,
    IReadOnlyList<AiCallRecentEntry> RecentCalls);

public sealed record AiCallHourlyBucket(int HoursAgo, int Total, int Failed);

public sealed record AiCallLatencyBucket(string Label, int LowerMs, int? UpperMs, int Count);

public sealed record AiCallErrorBucket(string ErrorType, int Count);

/// <summary>按模型聚合（调用数 + token 合计）</summary>
public sealed record AiCallModelBucket(string Model, int Count, int TotalTokens);

public sealed record AiCallRecentEntry(
    long Id,
    long? MediaItemId,
    bool Success,
    int LatencyMs,
    string? ErrorType,
    DateTimeOffset Timestamp,
    double? Confidence,
    string? ErrorDetail,
    int? PromptTokens,
    int? CompletionTokens,
    string? Model,
    string? ChainId,
    int AttemptLevel);

/// <summary>AI 调用日志分页查询参数（GET query string 绑定）</summary>
/// <remarks>
/// Page 从 1 起；PageSize 钳到 [1,100]。Success/ErrorType/ChainId/From/To 均可选，组合为 AND 过滤。
/// 时间用 ISO-8601（含时区）；ChainId 用于「升级链还原」——传某行的 ChainId 拉出同链全部级别。
/// </remarks>
public sealed record AiCallLogQuery(
    int Page = 1,
    int PageSize = 20,
    bool? Success = null,
    string? ErrorType = null,
    string? ChainId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>AI 调用日志分页结果</summary>
public sealed record AiCallLogPageResponse(
    long ProviderId,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<AiCallLogEntry> Items);

/// <summary>AI 调用日志单行（含请求/响应原文，供详情抽屉与升级链还原）</summary>
public sealed record AiCallLogEntry(
    long Id,
    long? MediaItemId,
    bool Success,
    int LatencyMs,
    string? ErrorType,
    string? ErrorDetail,
    string? Model,
    int? PromptTokens,
    int? CompletionTokens,
    double? Confidence,
    int? HttpStatus,
    string? ChainId,
    int AttemptLevel,
    bool IsPrimary,
    string? RequestText,
    string? ResponseText,
    DateTimeOffset Timestamp);
