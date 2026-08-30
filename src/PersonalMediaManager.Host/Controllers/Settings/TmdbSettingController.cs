using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Tmdb;
using PersonalMediaManager.Application.Services.Tmdb;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>TMDB / TMDB 配置</summary>
/// <remarks>
/// Tmdb_Setting 单例（Id=1）；ApiKey 走 DataProtection 加密；响应仅 hasApiKey 布尔。
/// /test 真发 GET /3/configuration；/cache/clear 清两张缓存表并返回删除条数。仅 Admin。
/// </remarks>
[ApiController]
[Route("settings/tmdb")]
public sealed class TmdbSettingController : ApiControllerBase
{
    private readonly ITmdbSettingService _service;

    public TmdbSettingController(ITmdbSettingService service)
    {
        _service = service;
    }

    /// <summary>Get TMDB / 读取</summary>
    /// <remarks>
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "hasApiKey": false, "language": "zh-CN", "fallbackLanguage": "en-US", "candidateThreshold": 3, "rateLimitPerSecond": 40, "metadataCacheHours": 24, "searchCacheMinutes": 60, "scoreWeightTitle": 0.5, "scoreWeightYear": 0.3, "scoreWeightPopularity": 0.1, "scoreWeightLanguage": 0.1, "createdAt": "...", "updatedAt": "..." }, "requestId": "..." }
    /// </code>
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">单例配置</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<TmdbSettingResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        TmdbSettingResponse data = await _service.GetAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update TMDB / 更新</summary>
    /// <remarks>
    /// 请求体（apiKey 三态：null=不变 / ""=清空 / 非空=替换）：
    /// <code>
    /// { "apiKey": "v4-bearer-token", "language": "zh-CN", "fallbackLanguage": "en-US", "candidateThreshold": 3, "rateLimitPerSecond": 40, "metadataCacheHours": 24, "searchCacheMinutes": 60, "scoreWeightTitle": 0.5, "scoreWeightYear": 0.3, "scoreWeightPopularity": 0.1, "scoreWeightLanguage": 0.1 }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — 语言为空 / 数值越界 / 权重之和 ≠ 1.0
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<TmdbSettingResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateTmdbSettingRequest req, CancellationToken ct)
    {
        TmdbSettingResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Test TMDB / 连通性</summary>
    /// <remarks>
    /// 调 GET https://api.themoviedb.org/3/configuration（Bearer ApiKey）。
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "success": true, "httpStatus": 200, "elapsedMilliseconds": 234.5, "errorMessage": null }, "requestId": "..." }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — 尚未配置 ApiKey
    /// 网络 / 鉴权失败时 success=false 且 errorMessage 携带原因，不抛 1000。
    /// </remarks>
    /// <response code="200">测试完成（具体结果看 data.success）</response>
    [HttpPost("test")]
    [ProducesResponseType<ApiResponse<TestTmdbResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        TestTmdbResponse data = await _service.TestAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Clear TMDB cache / 清缓存</summary>
    /// <remarks>
    /// 清 Tmdb_SearchCache + Tmdb_MetadataCache 两张表。
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "searchRowsDeleted": 12, "metadataRowsDeleted": 34 }, "requestId": "..." }
    /// </code>
    /// </remarks>
    /// <response code="200">清缓存成功</response>
    [HttpPost("cache/clear")]
    [ProducesResponseType<ApiResponse<ClearTmdbCacheResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearCache(CancellationToken ct)
    {
        ClearTmdbCacheResponse data = await _service.ClearCacheAsync(ct);
        return Ok(Wrap(data));
    }
}
