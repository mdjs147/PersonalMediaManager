using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Host.Controllers.Diagnostics;

/// <summary>解析诊断本地导出</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("diagnostics/parse")]
public sealed class ParseDiagnosticsController(ParseDiagnosticFileSink sink) : ApiControllerBase
{
    /// <summary>导出已保留的解析证据</summary>
    /// <remarks>
    /// 请求：GET ?runId=32位十六进制，或 ?scanRunId=32位十六进制，或 ?mediaItemId=正整数，三者只选其一。无路径入参。
    /// 成功：{ "code":0,"data":{ "events":[],"completeness":{} },"requestId":"..." }。
    /// 错误码：1000 参数无效；9000 存储读取失败。
    /// 错误：{ "code":1000,"message":"参数无效","data":null,"requestId":"..." }。
    /// 返回的是本机保留窗口内的脱敏事实，缺口在 completeness 明示，不保证完整重放。
    /// </remarks>
    /// <response code="200">包含完整性标记的回放包</response>
    [HttpGet("export")]
    [ProducesResponseType<ApiResponse<ParseReplayExport>>(StatusCodes.Status200OK)]
    public IActionResult Export([FromQuery] string? runId = null, [FromQuery] long? mediaItemId = null, [FromQuery] string? scanRunId = null)
    {
        try { return Ok(Wrap(sink.Export(runId, mediaItemId, scanRunId, HttpContext.RequestAborted))); }
        catch (ArgumentException ex) { throw new BusinessException(ex.Message); }
    }
}
