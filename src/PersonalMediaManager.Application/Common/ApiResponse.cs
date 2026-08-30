namespace PersonalMediaManager.Application.Common;

/// <summary>API 响应统一外层信封（唯一运行时信封 + OpenAPI 契约类型）</summary>
/// <remarks>
/// 一类两用，合一杜绝字段漂移：
/// 1) 运行时唯一信封：成功路径由 ApiControllerBase.Wrap 构造，错误路径由 ExceptionHandlerMiddleware 构造，
///    一律经 <see cref="ApiResponse"/> 工厂方法生成，禁止再手写 { code, message, data, requestId } 匿名对象。
/// 2) OpenAPI 契约：用作 Controller 上 [ProducesResponseType&lt;ApiResponse&lt;TResponse&gt;&gt;] 的类型参数，
///    让 OpenAPI 把外层 code/message/data/requestId 与内层业务 DTO 一起描述，
///    openapi-typescript 生成的 schema.d.ts 中 content['application/json'] 才不会退化为 never。
/// 字段语义：
/// - code：见 <see cref="ApiCode"/>，0 = 成功
/// - message：人类可读说明，成功时固定 "ok"
/// - data：业务载荷；无载荷时为 null
/// - requestId：与 RequestIdMiddleware 写入的 HttpContext.Items["RequestId"] 同源
/// </remarks>
/// <typeparam name="T">业务数据载荷类型</typeparam>
public sealed record ApiResponse<T>
{
    /// <summary>业务码（0=成功，1000=业务失败，9000=服务器错误）</summary>
    public int Code { get; init; }

    /// <summary>人类可读说明，成功固定 "ok"</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>业务数据载荷</summary>
    public T? Data { get; init; }

    /// <summary>请求链路 ID（与响应头 X-Request-Id 同源）</summary>
    public string RequestId { get; init; } = string.Empty;
}

/// <summary>ApiResponse 信封工厂（统一信封的唯一构造入口）</summary>
/// <remarks>
/// 与泛型 <see cref="ApiResponse{T}"/> 同名、按元数共存（CLR 以反引号区分 ApiResponse 与 ApiResponse`1）。
/// 成功走 <see cref="Success{T}"/>；失败走 <see cref="Fail"/>（data 恒 null，code 取 1000/9000）。
/// </remarks>
public static class ApiResponse
{
    /// <summary>构造成功信封（code=0）</summary>
    public static ApiResponse<T> Success<T>(T data, string requestId, string message = "ok") =>
        new() { Code = ApiCode.Success, Message = message, Data = data, RequestId = requestId };

    /// <summary>构造失败信封（data 恒 null）</summary>
    public static ApiResponse<object?> Fail(int code, string message, string requestId) =>
        Fail(code, message, requestId, data: null);

    /// <summary>构造失败信封（带结构化 data，如字段级校验错误清单）</summary>
    public static ApiResponse<object?> Fail(int code, string message, string requestId, object? data) =>
        new() { Code = code, Message = message, Data = data, RequestId = requestId };
}
