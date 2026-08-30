using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Controllers;

/// <summary>API 控制器基类：统一 ApiResponse 信封包装 + RequestId 提取（r1 P3-7）</summary>
/// <remarks>
/// 旧实现把同一段 Wrap&lt;T&gt; 在 18 个 Controller 各复制一份，违反 DRY 且 RequestId 字面量散落：
/// 改 ControllerBase 派生 → 删每个 Controller 的 private Wrap&lt;T&gt; → 复用 protected Wrap&lt;T&gt;。
/// 字面量 "RequestId" 走 RequestIdMiddleware.ItemsKey，跟随中间件常量自动更新。
///
/// P2-r3.10：类级 [ProducesResponseType] 集中标 400/500，让 Scalar / openapi-typescript 文档对错误响应有 schema 描述。
/// PMM 实际运行时（§0.6）业务/服务错误**不走 4xx/5xx**，由 ApiResponse.code (1000/9000) 在 200 内区分；
/// 此处的 400/500 标注仅作为「客户端 SDK 类型约束的边界场景」存在（如 ASP.NET 框架在 Authorize / RateLimit / 模型绑定崩溃等
/// 极端路径上可能直接吐 4xx/5xx 而不经过 ExceptionHandlerMiddleware）。继承自此基类的 20 个 Controller 不需重复标注。
/// </remarks>
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status500InternalServerError)]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>统一 ApiResponse 信封包装：{ code:0, message:"ok", data, requestId }</summary>
    protected ApiResponse<T> Wrap<T>(T data) =>
        ApiResponse.Success(data, HttpContext.Items[RequestIdMiddleware.ItemsKey]?.ToString() ?? string.Empty);
}
