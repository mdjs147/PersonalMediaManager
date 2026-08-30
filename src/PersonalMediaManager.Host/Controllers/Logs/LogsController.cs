using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Logs;
using PersonalMediaManager.Application.Services.Logs;

namespace PersonalMediaManager.Host.Controllers.Logs;

/// <summary>Logs / 历史日志</summary>
/// <remarks>
/// 走文件分页读，不入库（需求文档 §3.11）。
/// 公开端点：日志查看对所有人开放（需求文档 §3.12）。
/// </remarks>
[ApiController]
[Route("logs")]
[AllowAnonymous]
public sealed class LogsController : ApiControllerBase
{
    private readonly ILogQueryService _service;

    public LogsController(ILogQueryService service) { _service = service; }

    /// <summary>List / 日志列表</summary>
    /// <remarks>
    /// 查询参数：page / pageSize（≤200）/ level / from / to / keyword / source。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "timestamp":"...", "level":"Information", "source":"PMM.X", "message":"..." } ], "total":12345, "page":1, "pageSize":50 }, "requestId":"..." }
    /// ```
    /// 说明：total 是匹配行总数（粗略估值）。
    /// 错误码：
    /// - 1000 BusinessError — 分页参数非法 / 日志级别取值非法 / 时间区间格式错误
    /// - 9000 ServerError — 日志文件读取失败
    /// </remarks>
    /// <response code="200">按时间倒序的分页结果</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<LogPage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] LogQuery query, CancellationToken ct)
    {
        LogPage data = await _service.ListAsync(query, ct);
        return Ok(Wrap(data));
    }
}
