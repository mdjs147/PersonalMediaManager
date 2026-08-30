using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Subtitles;
using PersonalMediaManager.Application.Services.Subtitles;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Subtitle / 字幕源配置</summary>
/// <remarks>
/// Subtitle_Setting 单例（Id=1）；Assrt Token 走 DataProtection 加密；响应仅 hasToken 布尔。
/// /test 真发 GET /v1/user/quota（Bearer Token）。仅 Admin（全局 FallbackPolicy）。
/// </remarks>
[ApiController]
[Route("settings/subtitle")]
public sealed class SubtitleSettingController : ApiControllerBase
{
    private readonly ISubtitleSettingService _service;

    public SubtitleSettingController(ISubtitleSettingService service)
    {
        _service = service;
    }

    /// <summary>Get subtitle / 读取</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "hasToken": false, "baseUrl": "https://api.assrt.net", "timeoutSeconds": 30, "useProxy": false }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 字幕配置单例缺失
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "字幕配置单例（Id=1）缺失，请检查数据库 Migration 种子", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">单例配置（Token 脱敏为 hasToken）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<SubtitleSettingResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        SubtitleSettingResponse data = await _service.GetAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update subtitle / 更新</summary>
    /// <remarks>
    /// 请求体（token 三态：null=不变 / ""=清空 / 非空=替换）：
    /// ```json
    /// { "token": "assrt-api-token", "baseUrl": "https://api.assrt.net", "timeoutSeconds": 30, "useProxy": false }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "hasToken": true, "baseUrl": "https://api.assrt.net", "timeoutSeconds": 30, "useProxy": false }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — BaseUrl 为空或非 http/https 绝对地址 / 超时秒数越界 [5,120] / 字幕配置单例缺失
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "BaseUrl 必须是 http/https 绝对地址", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">更新成功（返回脱敏后配置）</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<SubtitleSettingResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateSubtitleSettingRequest req, CancellationToken ct)
    {
        SubtitleSettingResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Test subtitle / 连通性</summary>
    /// <remarks>
    /// 请求体：空。调 GET {baseUrl}/v1/user/quota（Bearer Token），返回剩余配额。
    /// 成功响应（网络 / 鉴权失败时 success=false 且 errorMessage 携带原因，不抛 1000）：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "success": true, "quota": 200, "elapsedMilliseconds": 320, "errorMessage": null }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 尚未配置 Assrt Token / 字幕配置单例缺失
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "尚未配置 Assrt Token", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">测试完成（具体结果看 data.success）</response>
    [HttpPost("test")]
    [ProducesResponseType<ApiResponse<TestSubtitleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        TestSubtitleResponse data = await _service.TestAsync(ct);
        return Ok(Wrap(data));
    }
}
