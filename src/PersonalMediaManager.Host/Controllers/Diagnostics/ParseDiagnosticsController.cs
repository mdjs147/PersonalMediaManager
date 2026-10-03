using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Diagnostics;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Host.Controllers.Diagnostics;

/// <summary>解析诊断本地导出</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("diagnostics/parse")]
public sealed class ParseDiagnosticsController(ParseDiagnosticFileSink sink) : ApiControllerBase
{
    /// <summary>管理员选择的诊断采集级别</summary>
    public sealed record DiagnosticLevelRequest(string Level);

    /// <summary>读取解析诊断设置</summary>
    /// <remarks>
    /// 请求：GET /api/diagnostics/parse/settings，仅限管理员，无路径或正文入参。
    /// 成功：{ "code":0,"data":{ "level":"Standard","retentionDays":7,"maxArtifacts":256 },"requestId":"..." }。
    /// 返回当前级别、正文与事件容量边界、写入失败计数和隐私说明，不返回任何诊断正文或认证信息。
    /// 访问失败：401 未登录，403 非管理员；服务错误使用统一 ApiResponse 错误信封。
    /// </remarks>
    /// <response code="200">当前诊断级别与容量快照</response>
    [HttpGet("settings")]
    [ProducesResponseType<ApiResponse<ParseDiagnosticSettingsDto>>(StatusCodes.Status200OK)]
    public IActionResult Settings() => Ok(Wrap(sink.GetSettings()));

    /// <summary>保存解析诊断级别</summary>
    /// <remarks>
    /// 请求：PUT /api/diagnostics/parse/settings，正文示例 { "level":"Full" }，仅限管理员。
    /// 级别仅允许 Off、Standard、Detailed、Full；容量边界不通过此接口修改。
    /// 先持久化再应用，后续采集生效；切换期间在途请求可能仅保存部分阶段，不补回过去的正文。
    /// 成功：{ "code":0,"data":{ "level":"Full","retentionDays":7 },"requestId":"..." }。
    /// 错误码：1000 级别无效，9000 存储失败；401 未登录，403 非管理员。
    /// </remarks>
    /// <response code="200">已保存的诊断级别与容量快照</response>
    [HttpPut("settings")]
    [ProducesResponseType<ApiResponse<ParseDiagnosticSettingsDto>>(StatusCodes.Status200OK)]
    public IActionResult UpdateSettings([FromBody] DiagnosticLevelRequest request)
    {
        try { return Ok(Wrap(sink.SetLevel(request.Level))); }
        catch (ArgumentException ex) { throw new BusinessException(ex.Message); }
    }

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
