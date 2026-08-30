using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Host.Controllers.Diagnostics;

/// <summary>Diagnostics / 异常诊断</summary>
/// <remarks>
/// 异常中间件测试端点。
///
/// 鉴权：仅 Admin 角色可触发（继承全局 FallbackPolicy 即可，故无需显式 [Authorize]）；之前曾标 [AllowAnonymous]
/// 让匿名能故意 boom，与需求 §3.12「设置/账户/写操作强制 Admin」+ 安全自检 best practice 不符，本次纠正。
/// Test/Dev 环境跑集成测试时，测试夹具用 admin token 登录后即可访问；前端不应展示该 endpoint。
///
/// 请求体：query string ?kind=business|server
///
/// 成功响应：N/A（端点专门用于抛异常验证 ExceptionHandlerMiddleware 行为）
///
/// 错误码：
/// - 1000 BusinessError — kind=business 时
/// - 9000 ServerError — kind=server 时
///
/// 错误响应：
/// ```json
/// { "code": 1000, "message": "业务规则失败示例", "data": null, "requestId": "f9c..." }
/// ```
/// </remarks>
[ApiController]
[Route("diag/boom")]
public sealed class BoomController : ControllerBase
{
    /// <summary>Boom / 故意抛异常</summary>
    /// <remarks>
    /// Query：?kind=business → 抛 BusinessException 验证 1000；?kind=server（任意其它值）→ 抛 InvalidOperationException 验证 9000；
    /// ?kind=secret → 抛 BusinessException 内含 apiKey 片段，验证 ExceptionHandlerMiddleware 的 SensitiveDataRedactor 脱敏（P2-r3.9）。
    /// 端点本身永远走 ExceptionHandlerMiddleware 包装为 200 + code 区分，无成功响应体。
    /// </remarks>
    /// <response code="200">所有失败都走 ExceptionHandlerMiddleware 包装为 200 + code 区分</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public IActionResult Boom([FromQuery] string kind = "business")
    {
        if (kind == "business")
        {
            throw new BusinessException("业务规则失败示例");
        }
        if (kind == "secret")
        {
            // P2-r3.9：模拟 service 层异常 message 含敏感片段，验证 ExceptionHandlerMiddleware 的 Redactor 兜底
            throw new BusinessException("AI 校验失败：apiKey=sk-1234567890abcdef");
        }

        throw new InvalidOperationException("未预期异常示例");
    }
}
