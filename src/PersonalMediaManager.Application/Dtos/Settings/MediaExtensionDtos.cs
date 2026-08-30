using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Settings;

/// <param name="Id">主键</param>
/// <param name="Extension">扩展名（含前导点，如 .mkv）</param>
/// <param name="Description">说明</param>
/// <param name="Enabled">是否启用</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record MediaExtensionResponse(
    long Id,
    string Extension,
    string? Description,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Extension">扩展名（含前导点，如 .mkv）</param>
/// <param name="Description">说明（可选）</param>
/// <param name="Enabled">是否启用</param>
public sealed record CreateMediaExtensionRequest(
    [RequiredNotBlank(ErrorMessage = "扩展名不能为空")]
    [RegularExpression(@"^\.[^\s\\/]+$", ErrorMessage = "扩展名格式非法：须以 '.' 开头且不含空白或路径分隔符")]
    [MinLength(2, ErrorMessage = "扩展名至少 2 个字符（含点）")]
    [MaxLength(32, ErrorMessage = "扩展名长度不能超过 32 字符")]
    string Extension,
    [MaxLength(200, ErrorMessage = "说明长度不能超过 200 字符")]
    string? Description,
    bool Enabled = true);

/// <param name="Id">主键</param>
/// <param name="Extension">扩展名（含前导点）</param>
/// <param name="Description">说明（可选）</param>
/// <param name="Enabled">是否启用</param>
public sealed record UpdateMediaExtensionRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "扩展名不能为空")]
    [RegularExpression(@"^\.[^\s\\/]+$", ErrorMessage = "扩展名格式非法：须以 '.' 开头且不含空白或路径分隔符")]
    [MinLength(2, ErrorMessage = "扩展名至少 2 个字符（含点）")]
    [MaxLength(32, ErrorMessage = "扩展名长度不能超过 32 字符")]
    string Extension,
    [MaxLength(200, ErrorMessage = "说明长度不能超过 200 字符")]
    string? Description,
    bool Enabled);

/// <param name="Id">主键</param>
public sealed record DeleteMediaExtensionRequest(long Id);
