using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Category;

/// <summary>分类定义响应 DTO</summary>
/// <remarks>MediaItemCount 仅 List 端点填充（COUNT 子查询），Create/Update 等单条返回 0</remarks>
public sealed record CategoryDefinitionResponse(
    long Id,
    string Name,
    MediaType MediaType,
    string TargetRoot,
    int Priority,
    string? Description,
    int MediaItemCount,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateCategoryDefinitionRequest(
    [RequiredNotBlank(ErrorMessage = "分类 Name 不能为空")]
    [MaxLength(64, ErrorMessage = "分类 Name 长度不能超过 64 字符")]
    string Name,
    MediaType MediaType,
    [RequiredNotBlank(ErrorMessage = "TargetRoot 不能为空")]
    [MaxLength(500, ErrorMessage = "TargetRoot 长度不能超过 500 字符")]
    string TargetRoot,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority = 100,
    [MaxLength(200, ErrorMessage = "Description 长度不能超过 200 字符")]
    string? Description = null);

public sealed record UpdateCategoryDefinitionRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "分类 Name 不能为空")]
    [MaxLength(64, ErrorMessage = "分类 Name 长度不能超过 64 字符")]
    string Name,
    MediaType MediaType,
    [RequiredNotBlank(ErrorMessage = "TargetRoot 不能为空")]
    [MaxLength(500, ErrorMessage = "TargetRoot 长度不能超过 500 字符")]
    string TargetRoot,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority,
    [MaxLength(200, ErrorMessage = "Description 长度不能超过 200 字符")]
    string? Description,
    long RowVersion);

public sealed record DeleteCategoryDefinitionRequest(long Id);
