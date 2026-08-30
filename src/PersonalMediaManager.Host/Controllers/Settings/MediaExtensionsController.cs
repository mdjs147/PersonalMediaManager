using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Application.Services.Settings;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Media extensions / 媒体扩展名</summary>
/// <remarks>
/// System_MediaExtension CRUD；扩展名必须以 '.' 开头，小写归一化，唯一约束。
/// 变更后 IMediaExtensionProvider 内存缓存自动刷新，FileWatcherWorker / ScanService 立即生效。
/// 仅 Admin 可访问。
/// </remarks>
[ApiController]
[Route("settings/media-extensions")]
public sealed class MediaExtensionsController : ApiControllerBase
{
    private readonly IMediaExtensionService _service;

    public MediaExtensionsController(IMediaExtensionService service)
    {
        _service = service;
    }

    /// <summary>List extensions / 列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "extension":".mkv", "description":"内置默认", "enabled":true, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// </remarks>
    /// <response code="200">扩展名列表（按 Extension 字母排序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<MediaExtensionResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<MediaExtensionResponse> data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create extension / 新增</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "extension": ".hevc", "description": "HEVC 高效视频编码", "enabled": true }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 扩展名为空 / 不以 '.' 开头 / 含非法字符 / 已存在
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<MediaExtensionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateMediaExtensionRequest req, CancellationToken ct)
    {
        MediaExtensionResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update extension / 更新</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1, "extension": ".mkv", "description": "新备注", "enabled": false }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 校验失败 / 冲突
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<MediaExtensionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateMediaExtensionRequest req, CancellationToken ct)
    {
        MediaExtensionResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete extension / 删除</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1 }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">删除成功</response>
    [HttpPost("delete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete([FromBody] DeleteMediaExtensionRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }
}
