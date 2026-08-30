using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Application.Services.Settings;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>归档命名 / 命名模板</summary>
/// <remarks>
/// 自定义 Plex 归档命名模板（3 个文件名主体模板 + 季标题开关）；{tmdb-NNN} 锚点 / Season NN / SxxEyy 等刮削必需结构锁定不进模板。
/// /preview 用固定示例渲染（不落库）；模板非法返校验错误清单。仅 Admin。
/// </remarks>
[ApiController]
[Route("settings/archive-naming")]
public sealed class ArchiveNamingController : ApiControllerBase
{
    private readonly IArchiveNamingService _service;

    public ArchiveNamingController(IArchiveNamingService service)
    {
        _service = service;
    }

    /// <summary>Get naming / 读取</summary>
    /// <remarks>
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "movieNameTemplate": "{title} ({year})", "tvShowFolderTemplate": "{title} ({year})", "tvEpisodeNameTemplate": "{title} ({year}) - {episode}", "seasonFolderIncludeTitle": true }, "requestId": "..." }
    /// </code>
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">当前模板配置</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<ArchiveNamingResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        ArchiveNamingResponse data = await _service.GetAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update naming / 更新</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "movieNameTemplate": "{title} ({year})", "tvShowFolderTemplate": "{title} ({year})", "tvEpisodeNameTemplate": "{title} ({year}) - {episode}", "seasonFolderIncludeTitle": true }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — 模板为空 / 含 {tmdb 字面量 / 含路径分隔符 / 未知占位符 / 单集模板缺 {episode}
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateArchiveNamingRequest req, CancellationToken ct)
    {
        await _service.UpdateAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Preview naming / 预览</summary>
    /// <remarks>
    /// 请求体（草稿模板，不落库）：
    /// <code>
    /// { "movieNameTemplate": "{title} ({year})", "tvShowFolderTemplate": "{title} ({year})", "tvEpisodeNameTemplate": "{title} ({year}) - {episode}", "seasonFolderIncludeTitle": true }
    /// </code>
    /// 成功响应（含各模板校验错误 + 固定样例渲染路径）：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "movieErrors": [], "tvShowFolderErrors": [], "tvEpisodeErrors": [], "samples": [ { "label": "电影（多版本）", "relativePath": "盗梦空间 (2010) {tmdb-27205}\\盗梦空间 (2010) - 导演剪辑版 {tmdb-27205}.mkv", "error": null } ] }, "requestId": "..." }
    /// </code>
    /// 错误码：1000 — 三模板任一为空（绑定阶段拦截）
    /// </remarks>
    /// <response code="200">预览结果（含校验错误清单）</response>
    [HttpPost("preview")]
    [ProducesResponseType<ApiResponse<ArchiveNamingPreviewResponse>>(StatusCodes.Status200OK)]
    public IActionResult Preview([FromBody] ArchiveNamingPreviewRequest req)
    {
        ArchiveNamingPreviewResponse data = _service.Preview(req);
        return Ok(Wrap(data));
    }
}
