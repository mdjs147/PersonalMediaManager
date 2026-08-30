using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>当前请求上下文中的用户信息</summary>
/// <remarks>
/// 来源：JWT 中间件解析 Authorization 头 → ClaimsPrincipal。
/// 匿名请求（公开端点）IsAuthenticated=false，UserId=null。
/// Application 服务通过本接口拿用户，避免直接依赖 ASP.NET Core ClaimsPrincipal。
/// </remarks>
public interface ICurrentUser
{
    long? UserId { get; }
    string? Username { get; }
    UserRole? Role { get; }
    bool IsAuthenticated { get; }
}
