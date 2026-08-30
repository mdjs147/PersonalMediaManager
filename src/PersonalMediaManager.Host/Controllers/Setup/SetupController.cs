using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Setup;
using PersonalMediaManager.Application.Services.Setup;

namespace PersonalMediaManager.Host.Controllers.Setup;

/// <summary>Setup / 初始化向导</summary>
/// <remarks>
/// 所有端点 [AllowAnonymous]：首次启动时无任何用户，不能要求登录。
/// 流程：GET /setup/status → POST /setup/admin → POST /setup/complete。
/// 未完成时其它 Admin 端点访问由 SetupGuardMiddleware（Host）兜底返 1000「请先完成初始化」。
/// </remarks>
[ApiController]
[Route("setup")]
[AllowAnonymous]
public sealed class SetupController : ApiControllerBase
{
    private readonly ISetupService _setup;

    public SetupController(ISetupService setup)
    {
        _setup = setup;
    }

    /// <summary>Setup status / 状态</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "isCompleted": false, "hasAdmin": false }, "requestId": "..." }
    /// ```
    /// 错误码：无
    /// </remarks>
    /// <response code="200">返回 setup 是否完成 + 是否已有 Admin</response>
    [HttpGet("status")]
    [ProducesResponseType<ApiResponse<SetupStatusResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        SetupStatusResponse data = await _setup.GetStatusAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Checklist / 配置就绪度</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "isCompleted": false, "hasAdmin": true, "watchFolderCount": 0, "tmdbHasApiKey": false, "categoryTotalCount": 5, "categoryUnsetCount": 5, "aiProviderCount": 0 }, "requestId": "..." }
    /// ```
    /// 错误码：无
    /// </remarks>
    /// <response code="200">返回各必需配置就绪度（向导续配 + Dashboard 完成度卡片用）</response>
    [HttpGet("checklist")]
    [ProducesResponseType<ApiResponse<SetupChecklistResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Checklist(CancellationToken ct)
    {
        SetupChecklistResponse data = await _setup.GetChecklistAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Setup admin / 创建首个 Admin</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "username": "admin", "password": "secret-min-6" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "userId": 1, "username": "admin" }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 用户名为空 / 密码 &lt; 6 / 用户名已存在
    /// </remarks>
    /// <response code="200">创建成功；失败由 ExceptionHandler 包装为 code=1000</response>
    [HttpPost("admin")]
    [ProducesResponseType<ApiResponse<CreateAdminResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAdmin([FromBody] CreateAdminRequest req, CancellationToken ct)
    {
        CreateAdminResponse data = await _setup.CreateAdminAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Setup complete / 标记完成</summary>
    /// <remarks>
    /// 请求体：无
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 尚未创建管理员
    /// </remarks>
    /// <response code="200">标记成功</response>
    [HttpPost("complete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(CancellationToken ct)
    {
        await _setup.CompleteAsync(ct);
        return Ok(Wrap<object?>(null));
    }
}
