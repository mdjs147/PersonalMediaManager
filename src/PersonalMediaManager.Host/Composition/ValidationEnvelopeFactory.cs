using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Logging;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Composition;

/// <summary>模型绑定校验失败 → 统一 {code:1000} 信封</summary>
/// <remarks>
/// <c>[ApiController]</c> 在 Action 执行前自动校验请求 DTO 上的 DataAnnotations，失败时回调本工厂。
/// 默认行为吐 HTTP 400 + ProblemDetails，违反 §0.6「业务/服务错误走 200 + code」约定；
/// 故改写为 200 + <see cref="ApiResponse.Fail(int, string, string, object?)"/>：
/// message 汇总全部字段错误（「；」分隔），data 带结构化字段清单（前端可按字段定位）。
/// 与 ExceptionHandlerMiddleware 形成一致出口——A 类无状态字段校验走此处（不抛异常），
/// B/C/D/E/F（不存在 / 冲突 / 状态机 / 并发 / 外部失败）仍走 BusinessException + 中间件。
/// message 经 SensitiveDataRedactor 兜底脱敏，与中间件保持同一纵深防御。
/// </remarks>
public static class ValidationEnvelopeFactory
{
    /// <summary>把 ModelState 错误构造成统一失败信封（HTTP 200）</summary>
    public static IActionResult Create(ActionContext context)
    {
        string requestId = context.HttpContext.Items.TryGetValue(RequestIdMiddleware.ItemsKey, out object? rid)
            ? rid?.ToString() ?? string.Empty
            : string.Empty;

        List<ValidationFieldError> fields = [];
        foreach (KeyValuePair<string, ModelStateEntry> entry in context.ModelState)
        {
            foreach (ModelError error in entry.Value.Errors)
            {
                string msg = string.IsNullOrEmpty(error.ErrorMessage) ? "字段校验失败" : error.ErrorMessage;
                fields.Add(new ValidationFieldError(NormalizeField(entry.Key), msg));
            }
        }

        string message = fields.Count > 0
            ? string.Join("；", fields.Select(f => f.Message).Distinct())
            : "请求参数校验失败";
        message = SensitiveDataRedactor.Redact(message);

        ApiResponse<object?> body = ApiResponse.Fail(ApiCode.BusinessError, message, requestId, fields);
        return new ObjectResult(body) { StatusCode = StatusCodes.Status200OK };
    }

    // ModelState key 形如 "$.path"（JSON body 绑定）或 "Path"；统一裁前缀并转小驼峰，便于前端按字段定位
    private static string NormalizeField(string key)
    {
        if (string.IsNullOrEmpty(key))
            return key;
        string name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        return name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;
    }
}

/// <summary>字段级校验错误项（失败信封 data 元素）</summary>
public sealed record ValidationFieldError(string Field, string Message);
