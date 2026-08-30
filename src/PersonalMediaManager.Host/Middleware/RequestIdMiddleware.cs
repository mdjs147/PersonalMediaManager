using Microsoft.AspNetCore.Http;

namespace PersonalMediaManager.Host.Middleware;

/// <summary>每请求生成 RequestId（写入 HttpContext.Items + 响应头 X-Request-Id）</summary>
/// <remarks>
/// 1) 客户端可通过 X-Request-Id 请求头指定（便于跨服务追踪）；缺失时服务端 Guid 兜底。
/// 2) ExceptionHandlerMiddleware 与 ApiResponse 信封工厂从 HttpContext.Items["RequestId"] 取出回填到响应 body。
/// 3) Serilog 通过 LogContext.PushProperty("RequestId", ...) 让日志自带追踪 ID。
/// </remarks>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";
    public const string ItemsKey = "RequestId";

    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string requestId = context.Request.Headers.TryGetValue(HeaderName, out Microsoft.Extensions.Primitives.StringValues value)
                           && !string.IsNullOrWhiteSpace(value.ToString())
            ? value.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[ItemsKey] = requestId;
        context.Response.Headers[HeaderName] = requestId;

        await _next(context);
    }
}
