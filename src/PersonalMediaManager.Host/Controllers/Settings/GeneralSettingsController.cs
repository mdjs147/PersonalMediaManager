using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Application.Services.Settings;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Settings general / 通用设置</summary>
/// <remarks>
/// System_Setting KV 配置；按 Category（General/Parse/Tmdb/Log/Webhook）分组聚合。
/// 受保护 Key（如 System.SetupCompleted）禁止通过本端点修改。
/// </remarks>
[ApiController]
[Route("settings/general")]
public sealed class GeneralSettingsController : ApiControllerBase
{
    private readonly IGeneralSettingsService _service;

    public GeneralSettingsController(IGeneralSettingsService service)
    {
        _service = service;
    }

    /// <summary>List general / 列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "groups": { "General": [ { "key":"Web.Port", "value":"7288", "category":"General", "description": null } ] } }, "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// </remarks>
    /// <response code="200">按 Category 分组的 KV 列表</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<GroupedSettingsResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        GroupedSettingsResponse data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update general / 批量更新</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "items": [ { "key": "Web.Port", "value": "8080" }, { "key": "Log.Level", "value": "Debug" } ] }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — items 为空 / Key 为空 / Key 受保护
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateGeneralRequest req, CancellationToken ct)
    {
        await _service.UpdateAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Test ffmpeg / 自检</summary>
    /// <remarks>
    /// 解析 Audio_FfmpegPath（目录或 exe）→ 各跑 ffprobe/ffmpeg -version 验证可用。
    /// 请求体（pathOverride：非空测当前未保存值 / 省略或 null 测已存路径）：
    /// ```json
    /// { "pathOverride": "D:\\tools\\ffmpeg\\bin" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "success": true, "ffprobe": { "runnable": true, "path": "D:\\tools\\ffmpeg\\bin\\ffprobe.exe", "version": "ffprobe version 6.1.1", "error": null }, "ffmpeg": { "runnable": true, "path": "D:\\tools\\ffmpeg\\bin\\ffmpeg.exe", "version": "ffmpeg version 6.1.1", "error": null }, "message": "ffmpeg 与 ffprobe 均可用：ffmpeg version 6.1.1" }, "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// 工具未配置 / 缺失 / 不可执行时统一返 200 + success=false（message 携带原因），不抛 1000。
    /// 通用错误响应：
    /// ```json
    /// { "code": 9000, "message": "服务器错误", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">自检完成（具体结果看 data.success）</response>
    [HttpPost("test-ffmpeg")]
    [ProducesResponseType<ApiResponse<TestFfmpegResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestFfmpeg([FromBody] TestFfmpegRequest req, CancellationToken ct)
    {
        TestFfmpegResponse data = await _service.TestFfmpegAsync(req, ct);
        return Ok(Wrap(data));
    }
}
