using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>AI providers / AI 提供商</summary>
/// <remarks>
/// Parse_AiProvider CRUD + /test 连通性 + /enable 手动解禁 + /reset-quota 重置套餐用量；ApiKey 走 DataProtection 加密存库；
/// IsPrimary 全表唯一（保存时旧主自动降级）；Ollama 允许 ApiKey 空，其他类型必须提供。
/// 仅 Admin。
/// </remarks>
[ApiController]
[Route("settings/ai-providers")]
public sealed class ParseAiProvidersController : ApiControllerBase
{
    private readonly IParseAiProviderService _service;

    public ParseAiProvidersController(IParseAiProviderService service)
    {
        _service = service;
    }

    /// <summary>List providers / 列表</summary>
    /// <remarks>
    /// 响应仅暴露 hasApiKey 布尔，不回显密文 / 明文。
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": [ { "id":1, "name":"deepseek", "type":"DeepSeek", "baseUrl":"https://api.deepseek.com", "hasApiKey":true, "model":"deepseek-chat", "isPrimary":true, "priority":100, "enabled":true, "disabledUntil": null, "timeoutSeconds":30, "extraOptions": null, "createdAt":"...", "updatedAt":"..." } ], "requestId": "..." }
    /// </code>
    /// 错误码：401 / 403
    /// </remarks>
    /// <response code="200">AI 提供商列表（按 IsPrimary 优先 + Priority 升序）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ParseAiProviderResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<ParseAiProviderResponse> data = await _service.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create provider / 新增</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "name": "deepseek", "type": "DeepSeek", "baseUrl": "https://api.deepseek.com", "apiKey": "sk-xxx", "model": "deepseek-chat", "isPrimary": true, "priority": 100, "enabled": true, "timeoutSeconds": 30, "extraOptions": null }
    /// </code>
    /// 可选套餐配额：终身 quotaCallLimit / quotaTokenLimit / quotaExpiresAt；周期滚动 quotaPeriod（None/Daily/Weekly/Monthly）+ quotaPeriodTimeZone + quotaPeriodCallLimit / quotaPeriodTokenLimit（均 null=不限）。
    /// 错误码：
    /// - 1000 BusinessError — 重名 / BaseUrl 非 http(s) URL / 非 Ollama 缺 ApiKey / ExtraOptions 非 JSON / 校验失败
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<ParseAiProviderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateParseAiProviderRequest req, CancellationToken ct)
    {
        ParseAiProviderResponse data = await _service.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update provider / 更新</summary>
    /// <remarks>
    /// 请求体同 Create + id。apiKey 三态：null = 保持不变（不重发密钥也能改其他字段）；"" = 清空（仅 Ollama 允许）；非空 = 重新加密。
    /// 套餐配额为全量语义（null=清除该限额）；切换 quotaPeriod 粒度时服务端自动重置本周期计量。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 重名 / 校验失败
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<ParseAiProviderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateParseAiProviderRequest req, CancellationToken ct)
    {
        ParseAiProviderResponse data = await _service.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete provider / 删除</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "id": 1 }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">删除成功</response>
    [HttpPost("delete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete([FromBody] DeleteParseAiProviderRequest req, CancellationToken ct)
    {
        await _service.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Test provider / 真发 1 次最小请求</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "id": 1 }
    /// </code>
    /// 探测策略：Ollama → GET /api/tags；其他 → POST /v1/chat/completions（messages=[ping], max_tokens=1）。
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": { "success": true, "httpStatus": 200, "elapsedMilliseconds": 123.4, "errorMessage": null, "responseSnippet": "..." }, "requestId": "..." }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// 网络 / 鉴权失败 时 success=false 且 errorMessage 携带原因，不抛 1000。
    /// </remarks>
    /// <response code="200">测试完成（具体结果看 data.success）</response>
    [HttpPost("test")]
    [ProducesResponseType<ApiResponse<TestParseAiProviderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test([FromBody] TestParseAiProviderRequest req, CancellationToken ct)
    {
        TestParseAiProviderResponse data = await _service.TestAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Stats / 调用统计</summary>
    /// <remarks>
    /// Query：windowHours（默认 24，可选 1-168）。聚合 Audit_AiCall 表生成调用次数、
    /// 成功/失败数、平均/P95 延迟（仅 Success 计入）、小时桶分布、固定延迟桶、错误类型分布、最近 12 条。
    /// 成功响应：
    /// <code>
    /// { "code":0,"message":"ok","data":{"providerId":1,"windowHours":24,"totalCalls":120,"successCount":108,"failedCount":12,"avgLatencyMs":820.4,"p95LatencyMs":2100,"hourlyBuckets":[{"hoursAgo":0,"total":5,"failed":1},...],"latencyHistogram":[...],"errorBreakdown":[...],"recentCalls":[...]},"requestId":"..."}
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / windowHours 越界
    /// </remarks>
    /// <response code="200">聚合统计</response>
    [HttpGet("{id:long}/stats")]
    [ProducesResponseType<ApiResponse<AiProviderStatsResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats([FromRoute] long id, [FromQuery] int windowHours = 24, CancellationToken ct = default)
    {
        AiProviderStatsResponse data = await _service.GetStatsAsync(id, windowHours, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Call logs / 调用日志</summary>
    /// <remarks>
    /// 分页查询 Audit_AiCall 调用日志（含请求/响应原文，供失败诊断与升级链还原）。
    /// Query：page（默认 1）、pageSize（默认 20，钳 [1,100]）、success、errorType、chainId、from、to（ISO-8601）。
    /// 成功响应：
    /// <code>
    /// { "code":0,"message":"ok","data":{"providerId":1,"page":1,"pageSize":20,"total":120,"items":[{"id":9001,"mediaItemId":42,"success":false,"latencyMs":820,"errorType":"LowConfidence","errorDetail":"...","model":"deepseek-chat","promptTokens":210,"completionTokens":36,"confidence":0.55,"httpStatus":200,"chainId":"ab12...","attemptLevel":1,"isPrimary":true,"requestText":"文件名：...","responseText":"{...}","timestamp":"..."}]},"requestId":"..." }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">分页调用日志</response>
    [HttpGet("{id:long}/logs")]
    [ProducesResponseType<ApiResponse<AiCallLogPageResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logs([FromRoute] long id, [FromQuery] AiCallLogQuery query, CancellationToken ct)
    {
        AiCallLogPageResponse data = await _service.GetLogsAsync(id, query, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Enable provider / 手动解禁</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "id": 1 }
    /// </code>
    /// 清 DisabledUntil（不动 Enabled 标志，保留用户显式禁用语义）。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">解禁成功</response>
    [HttpPost("enable")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Enable([FromBody] EnableParseAiProviderRequest req, CancellationToken ct)
    {
        await _service.EnableAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Reset quota / 重置套餐用量</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "id": 1 }
    /// </code>
    /// 清零 QuotaUsedCalls / QuotaUsedTokens 并清除 QuotaExceededAt（配额禁用解除），用于开始新套餐周期 / 续购；
    /// 周期滚动额度的已用计数（QuotaPeriodUsedCalls / QuotaPeriodUsedTokens）与 QuotaPeriodResetAt 一并清零重置；
    /// 限额配置与 Enabled / DisabledUntil 均不改动。
    /// 成功响应：
    /// <code>
    /// { "code": 0, "message": "ok", "data": null, "requestId": "..." }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// <code>
    /// { "code": 1000, "message": "AI 提供商 Id=1 不存在", "data": null, "requestId": "..." }
    /// </code>
    /// </remarks>
    /// <response code="200">重置成功</response>
    [HttpPost("reset-quota")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetQuota([FromBody] ResetQuotaParseAiProviderRequest req, CancellationToken ct)
    {
        await _service.ResetQuotaAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }
}
