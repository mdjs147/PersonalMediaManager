using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Category;
using PersonalMediaManager.Application.Services.Category;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Match rules / 分类匹配规则</summary>
/// <remarks>
/// Category_MatchRule CRUD；Conditions 列存 JSON 条件树（all/any 嵌套 + 叶子 {field,op,value}）；
/// 求值字段仅 type/originalLanguage/originCountry/genres，op 仅 eq/notEq/in/notIn/contains（见 CategoryRuleEvaluator）；
/// 保存时仅做 JSON 合法性校验，深层 Schema 校验放 D 阶段 ClassifyService。
/// Priority 越小越先匹配；CategoryId CASCADE：删除分类时自动清。仅 Admin。
/// </remarks>
[ApiController]
[Route("settings/category-match-rules")]
public sealed class CategoryMatchRulesController : ApiControllerBase
{
    private readonly ICategoryMatchRuleService _service;

    public CategoryMatchRulesController(ICategoryMatchRuleService service)
    {
        _service = service;
    }

    /// <summary>List rules / 列表</summary>
    /// <remarks>
    /// 可选 query：`?categoryId=1` 过滤指定分类的规则；不传则返回全部。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "categoryId":1, "name":"全部归电影", "conditions":"{}", "priority":100, "enabled":true, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// ```
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">规则列表（按 CategoryId / Priority 升序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<CategoryMatchRuleResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] long? categoryId, CancellationToken ct)
    {
        IReadOnlyList<CategoryMatchRuleResponse> data = await _service.ListAsync(categoryId, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create rule / 新增</summary>
    /// <remarks>
    /// 请求体（conditions 为规则条件 JSON 字符串，叶子形如 {field,op,value}，由 CategoryRuleEvaluator 求值）：
    /// ```json
    /// { "categoryId": 1, "name": "默认", "conditions": "{\"all\":[{\"field\":\"type\",\"op\":\"eq\",\"value\":\"movie\"}]}", "priority": 100, "enabled": true }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — CategoryId 不存在 / Conditions 非 JSON / 校验失败
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<CategoryMatchRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateCategoryMatchRuleRequest req, CancellationToken ct)
    {
        CategoryMatchRuleResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update rule / 更新</summary>
    /// <remarks>
    /// 请求体同 Create + id。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / CategoryId 不存在 / Conditions 非 JSON
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<CategoryMatchRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateCategoryMatchRuleRequest req, CancellationToken ct)
    {
        CategoryMatchRuleResponse data = await _service.UpdateAsync(req, ct);
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
    public async Task<IActionResult> Delete([FromBody] DeleteCategoryMatchRuleRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }
}
