using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Parse rules / 解析规则</summary>
/// <remarks>
/// Parse_Rule CRUD + /test 即席测试；保存时 Regex(Compiled, Timeout=500ms) 试编译，失败 → 1000；
/// /test 不读 DB，纯内存对 (pattern, sample) 跑 Regex.Match，返回命名捕获组。
/// 仅 Admin。
/// </remarks>
[ApiController]
[Route("settings/parse-rules")]
public sealed class ParseRulesController : ApiControllerBase
{
    private readonly IParseRuleService _service;

    public ParseRulesController(IParseRuleService service)
    {
        _service = service;
    }

    /// <summary>List rules / 列表</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "name":"标准电影", "scope":"FileName", "pattern":"...", "defaultType":"movie", "forceType":false, "priority":100, "confidenceBonus":0.1, "enabled":true, "description": null, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// ```
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">解析规则列表（按 Priority 升序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ParseRuleResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<ParseRuleResponse> data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>List builtin / 内置只读</summary>
    /// <remarks>
    /// 列出 RuleEngineService 在所有用户规则之后兜底运行的内置正则集合。
    /// 内置规则不可编辑、不可禁用、不可删除；本端点纯静态数据，不读数据库。
    ///
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":[ { "key":"SeasonEpisodeLatin", "name":"季集 SxxExx 拉丁格式", "description":"识别 S01E02 / s5e15 / SE01EP02…", "pattern":"[Ss]\\.?(?&lt;season&gt;\\d{1,2})…", "order":10, "samples":["BreakingBad.S01E02.1080p.mkv"] } ], "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">内置规则列表（按 Order 升序）</response>
    [HttpGet("builtin")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<BuiltinParseRuleResponse>>>(StatusCodes.Status200OK)]
    public IActionResult ListBuiltin()
    {
        IReadOnlyList<BuiltinParseRuleResponse> data = _service.ListBuiltins();
        return Ok(Wrap(data));
    }

    /// <summary>Create rule / 新增</summary>
    /// <remarks>
    /// 请求体（Pattern 推荐命名捕获 title / year / season / episode）：
    /// <code>
    /// { "name": "标准电影", "scope": "FileName", "pattern": "&lt;命名捕获正则&gt;", "defaultType": "movie", "forceType": false, "priority": 100, "confidenceBonus": 0.1, "enabled": true, "description": null }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — 重名 / 正则编译失败 / DefaultType 非法 / 置信度越界
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<ParseRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateParseRuleRequest req, CancellationToken ct)
    {
        ParseRuleResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update rule / 更新</summary>
    /// <remarks>
    /// 请求体同 Create + id。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 重名 / 正则编译失败 / 校验失败
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<ParseRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateParseRuleRequest req, CancellationToken ct)
    {
        ParseRuleResponse data = await _service.UpdateAsync(req, ct);
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
    public async Task<IActionResult> Delete([FromBody] DeleteParseRuleRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Import rules / 批量导入</summary>
    /// <remarks>
    /// 请求：multipart/form-data，file 字段为 JSON 数组（每条字段同 Create 请求体）。
    /// 查询参数：mode（Merge / Replace，默认 Merge）。Replace 高危：会先清空所有规则。
    /// 成功响应（部分失败也返 200 + code=0）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "added":12, "skipped":3, "deletedBeforeImport":0, "failures":[ { "index":7, "name":"...", "message":"正则编译失败：..." } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 文件缺失 / JSON 解析失败 / 数量超过 500
    /// </remarks>
    /// <response code="200">导入结果，含 added/skipped/failures 明细</response>
    [HttpPost("import")]
    [ProducesResponseType<ApiResponse<ImportParseRulesResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(
        IFormFile? file,
        [FromQuery] ParseRuleImportMode mode = ParseRuleImportMode.Merge,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            throw new BusinessException("未上传文件");
        }
        await using Stream input = file.OpenReadStream();
        ImportParseRulesResult data = await _service.ImportAsync(input, mode, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Test rule / 即席测试</summary>
    /// <remarks>
    /// 请求体（pattern + sample，不需要 id；不读 DB）：
    /// <code>
    /// { "pattern": "&lt;命名捕获正则&gt;", "sample": "Inception.2010.1080p.BluRay.x264.mkv" }
    /// </code>
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "matched": true, "groups": { "title": "Inception", "year": "2010" }, "elapsedMilliseconds": 0.123, "errorMessage": null }, "requestId": "..." }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — pattern 为空 / sample 为 null
    /// 正则编译失败 / 匹配超时 时 matched=false 且 errorMessage 携带原因，不抛 1000。
    /// </remarks>
    /// <response code="200">测试完成（具体结果看 data.matched）</response>
    [HttpPost("test")]
    [ProducesResponseType<ApiResponse<TestParseRuleResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test([FromBody] TestParseRuleRequest req, CancellationToken ct)
    {
        TestParseRuleResponse data = await _service.TestAsync(req, ct);
        return Ok(Wrap(data));
    }
}
