using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Watch;
using PersonalMediaManager.Application.Services.Watch;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Ignore rules / 忽略规则</summary>
/// <remarks>
/// Watch_IgnoreRule CRUD；Type=Extension 时 Pattern 必须 '.' 开头；
/// 同 Type+Pattern 唯一；初始 16 条扩展名种子（.part / .torrent / .xltd 等下载器临时/辅助文件）由 Migration HasData 注入。
/// 仅 Admin 可访问。
/// </remarks>
[ApiController]
[Route("settings/watch/ignore-rules")]
public sealed class WatchIgnoreRulesController : ApiControllerBase
{
    private readonly IWatchIgnoreRuleService _service;

    public WatchIgnoreRulesController(IWatchIgnoreRuleService service)
    {
        _service = service;
    }

    /// <summary>List rules / 列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "type":"Extension", "pattern":".part", "description":"默认忽略下载中临时文件", "enabled":true, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// </remarks>
    /// <response code="200">忽略规则列表（按 Type / Pattern 排序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<WatchIgnoreRuleResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<WatchIgnoreRuleResponse> data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create rule / 新增</summary>
    /// <remarks>
    /// 请求体（Extension）：
    /// ```json
    /// { "type": "Extension", "pattern": ".aria2", "description": "aria2 下载中", "enabled": true }
    /// ```
    /// 请求体（Keyword）：
    /// ```json
    /// { "type": "Keyword", "pattern": "sample", "description": "样片", "enabled": true }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Pattern 为空 / 非 '.' 开头（Extension）/ 含空白或分隔符 / 同 Type+Pattern 已存在
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<WatchIgnoreRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateWatchIgnoreRuleRequest req, CancellationToken ct)
    {
        WatchIgnoreRuleResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update rule / 更新</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1, "type": "Extension", "pattern": ".part", "description": "新备注", "enabled": false }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 校验失败 / 冲突
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<WatchIgnoreRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateWatchIgnoreRuleRequest req, CancellationToken ct)
    {
        WatchIgnoreRuleResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete rule / 删除</summary>
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
    public async Task<IActionResult> Delete([FromBody] DeleteWatchIgnoreRuleRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }
}
