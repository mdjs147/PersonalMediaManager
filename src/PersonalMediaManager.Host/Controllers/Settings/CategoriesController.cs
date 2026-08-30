using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Category;
using PersonalMediaManager.Application.Services.Category;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Categories / 媒体分类</summary>
/// <remarks>
/// Category_Definition CRUD；Name 全局唯一；删除前必须无 Media_Item 引用（→ 1000）。
/// MediaType=Both 仅本表使用（如纪录片可既归类电影也归类剧集）。
/// 仅 Admin 可访问。
/// </remarks>
[ApiController]
[Route("settings/categories")]
public sealed class CategoriesController : ApiControllerBase
{
    private readonly ICategoryDefinitionService _service;

    public CategoriesController(ICategoryDefinitionService service)
    {
        _service = service;
    }

    /// <summary>List categories / 列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "name":"电影", "mediaType":"Movie", "targetRoot":"D:/Plex/Movies", "priority":100, "description": null, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// ```
    /// 错误码：401 未认证 / 403 非 Admin
    /// </remarks>
    /// <response code="200">分类列表（按 Priority 升序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<CategoryDefinitionResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<CategoryDefinitionResponse> data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create category / 新增</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "name": "电影", "mediaType": "Movie", "targetRoot": "D:/Plex/Movies", "priority": 100, "description": null }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Name 为空 / 已存在 / TargetRoot 为空 / 优先级越界
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<CategoryDefinitionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateCategoryDefinitionRequest req, CancellationToken ct)
    {
        CategoryDefinitionResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update category / 更新</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1, "name": "电影改", "mediaType": "Movie", "targetRoot": "D:/Plex/Movies2", "priority": 50, "description": null }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 重名 / 校验失败
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<CategoryDefinitionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateCategoryDefinitionRequest req, CancellationToken ct)
    {
        CategoryDefinitionResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete category / 删除</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1 }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 仍被 Media_Item 引用
    /// </remarks>
    /// <response code="200">删除成功（关联匹配规则由 DB CASCADE 自动清理）</response>
    [HttpPost("delete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete([FromBody] DeleteCategoryDefinitionRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }
}
