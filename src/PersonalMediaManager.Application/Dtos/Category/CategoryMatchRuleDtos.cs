using System.ComponentModel.DataAnnotations;

namespace PersonalMediaManager.Application.Dtos.Category;

/// <summary>分类匹配规则响应 DTO</summary>
public sealed record CategoryMatchRuleResponse(
    long Id,
    long CategoryId,
    string? Name,
    string Conditions,
    int Priority,
    bool Enabled,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateCategoryMatchRuleRequest(
    long CategoryId,
    [MaxLength(100, ErrorMessage = "规则 Name 长度不能超过 100 字符")]
    string? Name,
    string Conditions,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority = 100,
    bool Enabled = true);

public sealed record UpdateCategoryMatchRuleRequest(
    long Id,
    long CategoryId,
    [MaxLength(100, ErrorMessage = "规则 Name 长度不能超过 100 字符")]
    string? Name,
    string Conditions,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority,
    bool Enabled,
    long RowVersion);

public sealed record DeleteCategoryMatchRuleRequest(long Id);
