using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Parse testcases / 解析测试集</summary>
/// <remarks>
/// Parse_TestCase CRUD + 从 Failed 历史灌入暂存区 + 状态流转（Promote / Disable / ResetToTriage）。
/// PR2 不含批量回归运行与 ApproveAsExpected（留 PR3 接 IRuleEngineService）。
/// 仅 Admin（全局 FallbackPolicy）。
/// </remarks>
[ApiController]
[Route("settings/parse-testcases")]
public sealed class ParseTestCasesController : ApiControllerBase
{
    private readonly IParseTestCaseService _service;

    public ParseTestCasesController(IParseTestCaseService service)
    {
        _service = service;
    }

    /// <summary>List cases / 列表</summary>
    /// <remarks>
    /// 查询参数：status（PendingTriage/Active/Disabled，省略=全部）、keyword（模糊匹配 SamplePath/ExpectedTitle）、
    /// page（&gt;=1）、pageSize（1~200，默认 50）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":42, "page":1, "pageSize":50, "items":[ { "id":1, "source":"Manual", "status":"Active", "samplePath":"F:\\...\\Inception.2010.mkv", "watchRootPath":"F:\\迅雷下载", "expectedTitle":"Inception", "expectedYear":2010, "expectedMediaType":"Movie", "expectedSeason":null, "expectedEpisode":null, "lastRunAt":null, "lastRunStatus":"NotRun", "lastRunResult":null, "lastMatchedRuleId":null, "aiVerdict":null, "aiSuggestedRulePattern":null, "notes":null, "rowVersion":0, "createdAt":"...", "updatedAt":"..." } ] }, "requestId":"..." }
    /// ```
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">分页结果，按 CreatedAt 倒序</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<ParseTestCaseListResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] ParseTestCaseStatus? status,
        [FromQuery] string? keyword,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        ParseTestCaseListResponse data = await _service.ListAsync(
            new ParseTestCaseListQuery(status, keyword, page, pageSize), ct);
        return Ok(Wrap(data));
    }

    /// <summary>Get case / 详情</summary>
    /// <remarks>
    /// 成功响应：单条 ParseTestCaseResponse（字段同 List items 单元素）。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">用例详情</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.GetByIdAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create case / 新增</summary>
    /// <remarks>
    /// 请求体（Source 固定 Manual / Status 固定 Active；WatchRootPath 不填则按监控目录最长前缀反查）：
    /// ```json
    /// { "samplePath":"F:\\迅雷下载\\Inception.2010.mkv", "watchRootPath":null, "expectedTitle":"Inception", "expectedYear":2010, "expectedMediaType":"Movie", "expectedSeason":null, "expectedEpisode":null, "notes":null }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — SamplePath 重复 / 长度越界 / ExpectedYear 越界 / ExpectedMediaType=Both
    /// </remarks>
    /// <response code="200">创建成功，返回完整 DTO</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateParseTestCaseRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update case / 更新</summary>
    /// <remarks>
    /// 请求体同 Create + id + rowVersion；只允许改基本字段（SamplePath/WatchRootPath/Expected*/Notes），
    /// 不允许改 Source / Status / LastRun* / Ai*（后两组机器维护；状态走专用接口）。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 乐观锁失败 / SamplePath 重名 / 字段校验失败
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateParseTestCaseRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete case / 删除</summary>
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
    public async Task<IActionResult> Delete([FromBody] DeleteParseTestCaseRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Import failed / 灌入失败历史</summary>
    /// <remarks>
    /// 请求体（limit 上限 200；sinceUtc 可选下限）：
    /// ```json
    /// { "limit": 50, "sinceUtc": null }
    /// ```
    /// 成功响应（部分失败仍返 200 + code=0）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "imported":12, "skipped":3, "failures":[ { "mediaItemId":42, "sourcePath":"F:\\...", "message":"..." } ] }, "requestId":"..." }
    /// ```
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">灌入结果，imported/skipped/failures 明细</response>
    [HttpPost("import-from-failed")]
    [ProducesResponseType<ApiResponse<ImportFromFailedResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportFromFailed([FromBody] ImportFromFailedRequest req, CancellationToken ct)
    {
        ImportFromFailedResult data = await _service.ImportFromFailedAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Promote / 升级为正式</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1, "rowVersion": 0 }
    /// ```
    /// 合法源状态：PendingTriage / Disabled → Active。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 乐观锁失败 / 已是 Active / 源状态非法
    /// </remarks>
    /// <response code="200">切换成功</response>
    [HttpPost("promote")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Promote([FromBody] TransitionParseTestCaseStatusRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.PromoteToActiveAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Disable / 停用</summary>
    /// <remarks>
    /// 请求体同 Promote。合法源状态：PendingTriage / Active → Disabled。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 乐观锁失败 / 已是 Disabled / 源状态非法
    /// </remarks>
    /// <response code="200">切换成功</response>
    [HttpPost("disable")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Disable([FromBody] TransitionParseTestCaseStatusRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.DisableAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Reset / 回退暂存</summary>
    /// <remarks>
    /// 请求体同 Promote。合法源状态：Active / Disabled → PendingTriage。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 乐观锁失败 / 已是 PendingTriage / 源状态非法
    /// </remarks>
    /// <response code="200">切换成功</response>
    [HttpPost("reset")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reset([FromBody] TransitionParseTestCaseStatusRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.ResetToTriageAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Run case / 单条回归</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1 }
    /// ```
    /// 调 RuleEngineService 跑一次解析 → 写 LastRunAt/LastRunResult/LastMatchedRuleId/LastRunStatus；
    /// LastRunStatus 取值：Pass（与 Expected 全一致）/ Fail（任一字段不一致）/ NotRun（未设任何 Expected 基线无法评判）。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">运行完成，返回完整 DTO（含 LastRun*）</response>
    [HttpPost("run")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Run([FromBody] RunParseTestCaseRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.RunAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Run batch / 批量回归</summary>
    /// <remarks>
    /// 请求体（status=null 跑所有；默认 Active；limit 上限 500）：
    /// ```json
    /// { "status": "Active", "limit": 200 }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "ran":120, "pass":98, "fail":12, "notComparable":10, "failures":[ { "id":42, "samplePath":"F:\\...", "message":"..." } ] }, "requestId":"..." }
    /// ```
    /// 单条抛异常不打断整体；失败明细在 failures 数组里。
    /// </remarks>
    /// <response code="200">批量结果，ran/pass/fail/notComparable/failures 明细</response>
    [HttpPost("run-all")]
    [ProducesResponseType<ApiResponse<RunParseTestCasesBatchResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunBatch([FromBody] RunParseTestCasesBatchRequest req, CancellationToken ct)
    {
        RunParseTestCasesBatchResult data = await _service.RunBatchAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Approve / 批准为基线</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1, "rowVersion": 0 }
    /// ```
    /// 把 LastRunResult JSON 的 title/year/mediaType/season/episode 字段拷贝到对应 Expected*；之后 LastRunStatus 刷为 Pass。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 乐观锁失败 / 用例尚未运行过回归 / LastRunResult JSON 损坏
    /// </remarks>
    /// <response code="200">批准成功</response>
    [HttpPost("approve")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve([FromBody] ApproveAsExpectedRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.ApproveAsExpectedAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>AI triage / AI 判定</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1 }
    /// ```
    /// 调 AI 判断该样本是否值得纳入测试集，结果写入 AiVerdict（JSON：worthAdding/reason/similarity/suggested/advisedAt），不自动改 Status。
    /// AI 调用复用主备 provider 链路；无可用 provider 或全失败 → 1000「AI 暂不可用」。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / AI 暂不可用 / AI 返回 JSON 无法解析
    /// </remarks>
    /// <response code="200">判定完成，返回含 aiVerdict 的完整 DTO</response>
    [HttpPost("triage")]
    [ProducesResponseType<ApiResponse<ParseTestCaseResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Triage([FromBody] TriageWithAiRequest req, CancellationToken ct)
    {
        ParseTestCaseResponse data = await _service.TriageWithAiAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>AI suggest rule / AI 生成规则</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "id": 1 }
    /// ```
    /// 调 AI 为该样本生成一条 .NET 正则解析规则（命名捕获 title/year/season/episode）；pattern 写入 AiSuggestedRulePattern。
    /// 返回完整建议（pattern/scope/defaultType/explanation），前端据此预填「新增解析规则」表单后调 /settings/parse-rules 落库。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "testCaseId":1, "pattern":"^(?&lt;title&gt;.+?)\\.(?&lt;year&gt;\\d{4})\\.", "scope":"FileName", "defaultType":"movie", "explanation":"..." }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / AI 暂不可用 / AI 未返回正则 / JSON 无法解析
    /// </remarks>
    /// <response code="200">生成完成，返回规则建议</response>
    [HttpPost("suggest-rule")]
    [ProducesResponseType<ApiResponse<RuleSuggestionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SuggestRule([FromBody] SuggestRuleRequest req, CancellationToken ct)
    {
        RuleSuggestionResponse data = await _service.SuggestRuleAsync(req, ct);
        return Ok(Wrap(data));
    }
}
