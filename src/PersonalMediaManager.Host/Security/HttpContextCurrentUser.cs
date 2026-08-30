using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Security;

/// <summary>ICurrentUser 的 Host 端实现：从 HttpContext.User 解析 Claims</summary>
/// <remarks>
/// JwtBearer 中间件解析 token 后填充 HttpContext.User；本类把 ClaimsPrincipal 翻译成 Application 可用的强类型。
/// 匿名请求（无 token / 公开端点）IsAuthenticated=false，UserId=null。
/// </remarks>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public bool IsAuthenticated => _accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;

    public long? UserId
    {
        get
        {
            string? raw = _accessor.HttpContext?.User.FindFirst("userId")?.Value;
            return long.TryParse(raw, out long id) ? id : null;
        }
    }

    public string? Username => _accessor.HttpContext?.User.FindFirst("username")?.Value;

    public UserRole? Role
    {
        get
        {
            string? raw = _accessor.HttpContext?.User.FindFirst("role")?.Value;
            return Enum.TryParse<UserRole>(raw, out UserRole role) ? role : null;
        }
    }
}
