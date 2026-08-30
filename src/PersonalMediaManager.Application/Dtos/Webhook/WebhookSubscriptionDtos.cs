using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Webhook;

/// <summary>Webhook 订阅响应 DTO（不暴露 Secret 密文）</summary>
/// <remarks>
/// DeliveredCount / FailedCount：按 SubscriptionId 聚合 Webhook_Delivery 状态总数，
/// 用于订阅卡片实时展示「已投递 / 失败」统计；Pending / Retrying 不计入任一桶。
/// </remarks>
public sealed record WebhookSubscriptionResponse(
    long Id,
    string Name,
    string Url,
    bool HasSecret,
    IReadOnlyList<string> Events,
    bool Enabled,
    int TimeoutSeconds,
    int DeliveredCount,
    int FailedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateWebhookSubscriptionRequest(
    [RequiredNotBlank(ErrorMessage = "Name 不能为空")]
    [MaxLength(64, ErrorMessage = "Name 长度不能超过 64 字符")]
    string Name,
    [RequiredNotBlank(ErrorMessage = "Url 不能为空")]
    [MaxLength(500, ErrorMessage = "Url 长度不能超过 500 字符")]
    [HttpUrl(ErrorMessage = "Url 必须是合法的 http / https URL")]
    string Url,
    string? Secret,
    [Required(ErrorMessage = "Events 至少包含 1 项事件")]
    [MinLength(1, ErrorMessage = "Events 至少包含 1 项事件")]
    IReadOnlyList<string> Events,
    bool Enabled = true,
    [Range(1, 60, ErrorMessage = "TimeoutSeconds 必须在 [1, 60] 范围内")]
    int TimeoutSeconds = 10);

public sealed record UpdateWebhookSubscriptionRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "Name 不能为空")]
    [MaxLength(64, ErrorMessage = "Name 长度不能超过 64 字符")]
    string Name,
    [RequiredNotBlank(ErrorMessage = "Url 不能为空")]
    [MaxLength(500, ErrorMessage = "Url 长度不能超过 500 字符")]
    [HttpUrl(ErrorMessage = "Url 必须是合法的 http / https URL")]
    string Url,
    string? Secret,
    [Required(ErrorMessage = "Events 至少包含 1 项事件")]
    [MinLength(1, ErrorMessage = "Events 至少包含 1 项事件")]
    IReadOnlyList<string> Events,
    bool Enabled,
    [Range(1, 60, ErrorMessage = "TimeoutSeconds 必须在 [1, 60] 范围内")]
    int TimeoutSeconds);

public sealed record DeleteWebhookSubscriptionRequest(long Id);

/// <summary>Webhook 总开关响应（false=全局关闭，归档不产生投递记录）</summary>
public sealed record WebhookEnabledResponse(bool Enabled);

/// <summary>设置 Webhook 总开关请求</summary>
public sealed record SetWebhookEnabledRequest(bool Enabled);
