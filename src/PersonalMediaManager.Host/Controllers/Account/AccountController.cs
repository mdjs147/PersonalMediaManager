using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Account;
using PersonalMediaManager.Application.Services.Account;

namespace PersonalMediaManager.Host.Controllers.Account;

/// <summary>Account / 账户管理（仅 Admin）</summary>
/// <remarks>
/// 列表 / 创建 / 删除 / 改密；fallback policy 已要求 Admin 角色。
/// 注：按 §0.5 红线只用 GET / POST，避免 PUT/DELETE/PATCH。
/// </remarks>
[ApiController]
[Route("account")]
public sealed class AccountController : ApiControllerBase
{
    private readonly IAccountService _account;
    private readonly ICurrentUser _currentUser;

    public AccountController(IAccountService account, ICurrentUser currentUser)
    {
        _account = account;
        _currentUser = currentUser;
    }

    /// <summary>Users list / 用户列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id": 1, "username": "admin", "role": "Admin", ... } ], "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// </remarks>
    /// <response code="200">用户列表</response>
    [HttpGet("users")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<UserListItem>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<UserListItem> data = await _account.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Users create / 创建用户</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "username": "viewer1", "password": "secret-min-6", "role": "Viewer" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "id": 2, "username": "viewer1", "role": "Viewer", "createdAt": "..." }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 用户名为空 / 密码 &lt; 6 / 用户名已存在
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost("users/create")]
    [ProducesResponseType<ApiResponse<UserListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        UserListItem data = await _account.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Users delete / 删除用户</summary>
    /// <remarks>
    /// 请求体：路由参数 id
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 用户不存在 / 不能删除最后一个管理员
    /// </remarks>
    /// <response code="200">删除成功</response>
    [HttpPost("users/{id:long}/delete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete([FromRoute] long id, CancellationToken ct)
    {
        await _account.DeleteAsync(id, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Password change / 改密</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "oldPassword": "...", "newPassword": "secret-min-6" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 旧密码错 / 新密码 &lt; 6 / 未认证
    /// </remarks>
    /// <response code="200">改密成功</response>
    [HttpPost("password/change")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        long userId = _currentUser.UserId ?? throw new BusinessException("未认证");
        await _account.ChangePasswordAsync(userId, req, ct);
        return Ok(Wrap<object?>(null));
    }
}
