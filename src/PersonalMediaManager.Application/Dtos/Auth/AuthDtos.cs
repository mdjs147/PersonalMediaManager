using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Auth;

public sealed record LoginRequest(
    [RequiredNotBlank(ErrorMessage = "用户名不能为空")]
    string Username,
    [RequiredNotBlank(ErrorMessage = "密码不能为空")]
    string Password);

public sealed record LoginResponse(string Token, UserSummary User);

public sealed record UserSummary(long Id, string Username, UserRole Role, DateTimeOffset? LastLoginAt);
