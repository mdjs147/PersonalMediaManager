using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Diagnostics;

namespace PersonalMediaManager.Host.Controllers.Diagnostics;

/// <summary>Health / 健康检查</summary>
/// <remarks>
/// 请求体：无（GET）
///
/// 成功响应：
/// ```json
/// { "code": 0, "message": "ok", "data": { "status": "ok" }, "requestId": "f9c..." }
/// ```
///
/// 错误码：无（本端点理论上不会失败；进程未起则返 502/无响应）
///
/// 错误响应：N/A（参考 §0.6 单条通用错误响应结构）
/// </remarks>
[ApiController]
[Route("health")]
[AllowAnonymous]
public sealed class HealthController : ApiControllerBase
{
    /// <summary>Health / 健康检查</summary>
    /// <remarks>
    /// 请求：GET 无参数。响应固定 { code: 0, message: "ok", data: { status: "ok" }, requestId: ... }。
    /// 端点用于 Launcher 启动后探活与浏览器跳转就绪判定；进程未起则返 502 / 无响应。
    /// </remarks>
    /// <response code="200">服务在线，返回 { code:0, data:{ status:'ok' } }</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<HealthStatusResponse>>(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        // r3 P3-r3.12 已并入统一信封：复用 ApiControllerBase.Wrap，requestId 改走 RequestIdMiddleware（消除 TraceIdentifier 漂移）
        return Ok(Wrap(new HealthStatusResponse("ok")));
    }
}
