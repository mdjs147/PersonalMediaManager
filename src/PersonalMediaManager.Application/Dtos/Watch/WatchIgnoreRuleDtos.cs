using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Watch;

/// <summary>忽略规则响应 DTO</summary>
public sealed record WatchIgnoreRuleResponse(
    long Id,
    IgnoreRuleType Type,
    string Pattern,
    string? Description,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateWatchIgnoreRuleRequest(
    IgnoreRuleType Type,
    [RequiredNotBlank(ErrorMessage = "Pattern 不能为空")]
    [MaxLength(200, ErrorMessage = "Pattern 长度不能超过 200 字符")]
    string Pattern,
    [MaxLength(200, ErrorMessage = "Description 长度不能超过 200 字符")]
    string? Description,
    bool Enabled = true);

public sealed record UpdateWatchIgnoreRuleRequest(
    long Id,
    IgnoreRuleType Type,
    [RequiredNotBlank(ErrorMessage = "Pattern 不能为空")]
    [MaxLength(200, ErrorMessage = "Pattern 长度不能超过 200 字符")]
    string Pattern,
    [MaxLength(200, ErrorMessage = "Description 长度不能超过 200 字符")]
    string? Description,
    bool Enabled);

public sealed record DeleteWatchIgnoreRuleRequest(long Id);
