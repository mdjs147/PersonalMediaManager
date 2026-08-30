using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Auth;
using PersonalMediaManager.Application.Services.Auth;

namespace PersonalMediaManager.Host.Controllers.Auth;

/// <summary>Auth / 认证</summary>
/// <remarks>
/// login / logout / me 三端点；login [AllowAnonymous]；logout 与 me [Authorize]（需已认证）。
/// 登录失败仅记审计不锁定（需求文档 §3.12）。
/// </remarks>
[ApiController]
[Route("auth")]
public sealed class AuthController : ApiControllerBase
{
    private readonly IAuthService _auth;
    private readonly ICurrentUser _currentUser;

    public AuthController(IAuthService auth, ICurrentUser currentUser)
    {
        _auth = auth;
        _currentUser = currentUser;
    }

    /// <summary>Login / 登录</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "username": "admin", "password": "secret" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "token": "ey...", "user": { "id": 1, "username": "admin", "role": "Admin", "lastLoginAt": "..." } }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 用户名/密码为空 / 用户名或密码错误
    /// </remarks>
    /// <response code="200">登录成功或失败（失败由 ExceptionHandler 包装为 code=1000）</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")] // 防口令在线爆破：策略名与 PmmHost.AddRateLimiter 同步（按 IP 每分钟 ≤10 次）；测试场景未注册该策略时属性自动空转
    [ProducesResponseType<ApiResponse<LoginResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        LoginResponse data = await _auth.LoginAsync(req, ip, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Logout / 登出</summary>
    /// <remarks>
    /// 请求体：无；需 Authorization 头。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// ```
    /// 错误码：401 未携带 token 由 JwtBearer 中间件直接拒绝（不走 ExceptionHandler）
    /// </remarks>
    /// <response code="200">登出成功</response>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        long userId = _currentUser.UserId ?? throw new BusinessException("未认证");
        string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _auth.LogoutAsync(userId, ip, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Me / 当前用户</summary>
    /// <remarks>
    /// 请求体：无；需 Authorization 头。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "id": 1, "username": "admin", "role": "Admin", "lastLoginAt": "..." }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未认证 / 用户不存在（被删除）
    /// </remarks>
    /// <response code="200">返回当前用户摘要</response>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<ApiResponse<UserSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        long userId = _currentUser.UserId ?? throw new BusinessException("未认证");
        UserSummary data = await _auth.GetMeAsync(userId, ct);
        return Ok(Wrap(data));
    }
}
