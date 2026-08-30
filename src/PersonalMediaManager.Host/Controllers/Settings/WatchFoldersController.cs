using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Watch;
using PersonalMediaManager.Application.Services.Watch;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Watch folders / 监控目录</summary>
/// <remarks>
/// Watch_Folder CRUD + 连通性测试；同一路径不可重复添加；
/// 删除前必须确认目录下无未完成的 Media_Item（in-flight），否则返 1000。
/// 仅 Admin 可访问（全局 FallbackPolicy）。
/// </remarks>
[ApiController]
[Route("settings/watch/folders")]
public sealed class WatchFoldersController : ApiControllerBase
{
    private readonly IWatchFolderService _service;

    public WatchFoldersController(IWatchFolderService service)
    {
        _service = service;
    }

    /// <summary>List folders / 列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "path":"D:/Downloads", "alias":"下载盘", "isTransit":true, "isNetworkShare":false, "enabled":true, "autoScan":true, "priority":100, "lastScanAt": null, "lastReachableAt": null, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// </remarks>
    /// <response code="200">监控目录列表（按 Priority 升序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<WatchFolderResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<WatchFolderResponse> data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create folder / 新增</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "path": "D:/Downloads", "alias": "下载盘", "isTransit": true, "isNetworkShare": false, "enabled": true, "autoScan": true, "priority": 100 }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "id":1, "path":"D:/Downloads", "...":"..." }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 路径为空 / 长度超限 / 已存在 / 优先级越界 / 别名超长
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<WatchFolderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateWatchFolderRequest req, CancellationToken ct)
    {
        WatchFolderResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update folder / 更新</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1, "path": "D:/Downloads2", "alias": "下载盘2", "isTransit": false, "isNetworkShare": false, "enabled": true, "autoScan": true, "priority": 50 }
    /// ```
    /// 成功响应同 Create。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 路径冲突 / 校验失败
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<WatchFolderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateWatchFolderRequest req, CancellationToken ct)
    {
        WatchFolderResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete folder / 删除</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1 }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 目录下尚有 in-flight Media_Item
    /// </remarks>
    /// <response code="200">删除成功</response>
    [HttpPost("delete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete([FromBody] DeleteWatchFolderRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Test folder / 连通性测试</summary>
    /// <remarks>
    /// 路径参数：监控目录 Id。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "reachable": true, "directoryExists": true, "sampleEntry": "D:/Downloads/foo.mkv", "errorMessage": null }, "requestId": "..." }
    /// ```
    /// 目录不存在或读取失败时 reachable=false 且 errorMessage 携带原因（不抛 1000）。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">测试完成（具体结果看 data.reachable）</response>
    [HttpGet("{id:long}/test")]
    [ProducesResponseType<ApiResponse<WatchFolderTestResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(long id, CancellationToken ct)
    {
        WatchFolderTestResponse data = await _service.TestAsync(id, ct);
        return Ok(Wrap(data));
    }
}
