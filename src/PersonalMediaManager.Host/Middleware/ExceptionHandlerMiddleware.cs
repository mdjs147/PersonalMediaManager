using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Exceptions;
using PersonalMediaManager.Host.Logging;

namespace PersonalMediaManager.Host.Middleware;

/// <summary>统一异常处理：BusinessException → 1000、其它 → 9000；body 含 requestId</summary>
/// <remarks>
/// 注册位置：在 SerilogRequestLogging 内层（PmmHost 已锁顺序为 RequestId → Serilog → ExceptionHandler）。
/// 为何不放最外层：放最外层时 Serilog 在 finally 取到的状态码是「异常未处理 → 框架默认 500」，
/// 业务异常会被误记成 [ERR] 5xx 污染告警；放 Serilog 内层后，ExceptionHandler 先把异常改写成 200/code=1000，
/// Serilog finally 看到的就是 200，日志噪音消失，客户端仍拿到统一 ApiResponse 包装。
/// 响应体格式：{ "code": int, "message": string, "data": null, "requestId": string }
/// HTTP 状态码统一 200（按 §0.6 约定：业务/服务错误不走 4xx/5xx，由 code 区分）。
/// </remarks>
public sealed class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlerMiddleware> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BusinessException ex)
        {
            _logger.LogInformation(ex, "业务异常：{Message}", ex.Message);
            await WriteJsonAsync(context, ApiCode.BusinessError, ex.Message);
        }
        catch (DomainException ex)
        {
            // 领域不变式被破坏（状态机非法跃迁、规则违反等）= 业务错误，按 1000 返
            // 避免 Application 层每个调用点都重复 try/catch 包成 BusinessException
            _logger.LogInformation(ex, "领域异常：{Message}", ex.Message);
            await WriteJsonAsync(context, ApiCode.BusinessError, ex.Message);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // EF 乐观锁失败（RowVersion 冲突）= 业务可恢复，引导前端「刷新重试」而非展示「服务器错误」
            _logger.LogInformation(ex, "并发冲突：{Message}", ex.Message);
            await WriteJsonAsync(context, ApiCode.BusinessError, "记录已被其他用户修改，请刷新后重试");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "未预期异常：{Message}", ex.Message);
            // 不向客户端泄漏内部异常细节；仅 message="服务器内部错误"
            await WriteJsonAsync(context, ApiCode.ServerError, "服务器内部错误");
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, int code, string message)
    {
        if (context.Response.HasStarted)
        {
            // 已开始写响应（可能在 Controller 内部异步流式响应）；中间件无法再覆盖
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        string requestId = context.Items.TryGetValue(RequestIdMiddleware.ItemsKey, out object? rid)
            ? rid?.ToString() ?? string.Empty
            : string.Empty;

        // P2-r3.9：业务/领域异常 message 在序列化前统一脱敏（ApiKey / Bearer Token / JSON 敏感字段）
        // service 层抛 throw new BusinessException($"AI 校验失败：apiKey={k}") 这类「带凭证片段的异常」会原文返前端，
        // 故在最终写入响应这一关再加一道兜底（与 SignalRSink + Serilog QueryString 形成纵深防御）
        string safeMessage = SensitiveDataRedactor.Redact(message);

        ApiResponse<object?> body = ApiResponse.Fail(code, safeMessage, requestId);

        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOpts));
    }
}
