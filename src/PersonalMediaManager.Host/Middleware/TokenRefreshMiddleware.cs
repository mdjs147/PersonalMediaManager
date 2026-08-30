using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Middleware;

/// <summary>无感续签中间件：exp - now &lt; 7 天 → 响应头 X-Token-Refresh 下发新 token</summary>
/// <remarks>
/// 需求文档 §3.12「无感续签」：前端 axios 拦截器读到本响应头即替换 localStorage。
/// 仅对已认证请求生效；新 token 直接由 claims 字段 (userId/username/role) 重新颁发，
/// 不构造 UserAccount 聚合（避免 Host 跨程序集访问 Entity.Id 的 protected internal setter）。
/// </remarks>
public sealed class TokenRefreshMiddleware
{
    public const string HeaderName = "X-Token-Refresh";

    private readonly RequestDelegate _next;

    public TokenRefreshMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IJwtTokenService tokenService)
    {
        await _next(context);

        if (context.Response.HasStarted) return;
        if (context.User.Identity?.IsAuthenticated != true) return;

        string? expClaim = context.User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        if (!long.TryParse(expClaim, out long expUnix)) return;

        DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        if (!tokenService.ShouldRefresh(expires)) return;

        if (!long.TryParse(context.User.FindFirst("userId")?.Value, out long userId)) return;
        string? username = context.User.FindFirst("username")?.Value;
        if (string.IsNullOrEmpty(username)) return;
        if (!Enum.TryParse<UserRole>(context.User.FindFirst("role")?.Value, out UserRole role)) return;

        string newToken = tokenService.Generate(userId, username, role);
        context.Response.Headers[HeaderName] = newToken;
    }
}
