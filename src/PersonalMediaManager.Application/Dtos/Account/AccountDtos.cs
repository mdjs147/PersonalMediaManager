using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Account;

public sealed record CreateUserRequest(
    [RequiredNotBlank(ErrorMessage = "用户名不能为空")]
    string Username,
    [RequiredNotBlank(ErrorMessage = "密码不能为空")]
    [MinLength(6, ErrorMessage = "密码长度至少 6 位")]
    string Password,
    UserRole Role);

public sealed record UserListItem(long Id, string Username, UserRole Role, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt);

public sealed record ChangePasswordRequest(
    string OldPassword,
    [RequiredNotBlank(ErrorMessage = "新密码不能为空")]
    [MinLength(6, ErrorMessage = "新密码长度至少 6 位")]
    string NewPassword);
