using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Webhook;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Controllers.Settings;

/// <summary>Webhooks / Webhook 订阅与出站日志</summary>
/// <remarks>
/// 订阅：CRUD（C.7），出站发送在 D 阶段 OutboxWorker 落地；
/// 出站日志：仅 GET 列表（分页 + 状态 / 订阅 ID 过滤），不暴露 Payload；
/// Secret 走 DataProtection 加密。仅 Admin。
/// </remarks>
[ApiController]
[Route("settings/webhooks")]
public sealed class WebhooksController : ApiControllerBase
{
    private readonly IWebhookSubscriptionService _subs;
    private readonly IWebhookDeliveryService _deliveries;

    public WebhooksController(IWebhookSubscriptionService subs, IWebhookDeliveryService deliveries)
    {
        _subs = subs;
        _deliveries = deliveries;
    }

    /// <summary>Get switch / 总开关状态</summary>
    /// <remarks>
    /// 读 Webhook 全局总开关；false=关闭时归档不产生任何 Webhook_Delivery。
    /// <code>
    /// { "code":0,"message":"ok","data":{"enabled":false},"requestId":"..."}
    /// </code>
    /// </remarks>
    /// <response code="200">总开关状态</response>
    [HttpGet("enabled")]
    [ProducesResponseType<ApiResponse<WebhookEnabledResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEnabled(CancellationToken ct)
    {
        bool enabled = await _subs.GetGlobalEnabledAsync(ct);
        return Ok(Wrap(new WebhookEnabledResponse(enabled)));
    }

    /// <summary>Set switch / 设置总开关</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "enabled": true }
    /// </code>
    /// 关闭后归档不再写 Webhook_Delivery（已入库的待投递仍由 OutboxWorker 处理完）。
    /// </remarks>
    /// <response code="200">设置成功，返回最新状态</response>
    [HttpPost("enabled")]
    [ProducesResponseType<ApiResponse<WebhookEnabledResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetEnabled([FromBody] SetWebhookEnabledRequest req, CancellationToken ct)
    {
        await _subs.SetGlobalEnabledAsync(req.Enabled, ct);
        return Ok(Wrap(new WebhookEnabledResponse(req.Enabled)));
    }

    /// <summary>List subs / 订阅列表</summary>
    /// <remarks>
    /// 响应仅 hasSecret 布尔。
    /// <code>
    /// { "code":0,"message":"ok","data":[{"id":1,"name":"plex","url":"https://...","hasSecret":true,"events":["media.archived"],"enabled":true,"timeoutSeconds":10,"createdAt":"...","updatedAt":"..."}],"requestId":"..."}
    /// </code>
    /// </remarks>
    /// <response code="200">订阅列表</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<WebhookSubscriptionResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSubs(CancellationToken ct)
    {
        IReadOnlyList<WebhookSubscriptionResponse> data = await _subs.ListAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Create sub / 新增订阅</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "name": "plex", "url": "https://hooks.example.com/plex", "secret": "shared-secret", "events": ["media.archived","media.failed"], "enabled": true, "timeoutSeconds": 10 }
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — 重名 / URL 非 http(s) / Events 空 / Timeout 越界
    /// </remarks>
    /// <response code="200">创建成功</response>
    [HttpPost]
    [ProducesResponseType<ApiResponse<WebhookSubscriptionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateSub([FromBody] CreateWebhookSubscriptionRequest req, CancellationToken ct)
    {
        WebhookSubscriptionResponse data = await _subs.CreateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Update sub / 更新订阅</summary>
    /// <remarks>
    /// 请求体同 Create + id；secret 三态 null=不变 / ""=清空 / 非空=替换。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / 校验失败
    /// </remarks>
    /// <response code="200">更新成功</response>
    [HttpPost("update")]
    [ProducesResponseType<ApiResponse<WebhookSubscriptionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSub([FromBody] UpdateWebhookSubscriptionRequest req, CancellationToken ct)
    {
        WebhookSubscriptionResponse data = await _subs.UpdateAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete sub / 删除订阅</summary>
    /// <remarks>
    /// 请求体：
    /// <code>
    /// { "id": 1 }
    /// </code>
    /// 删除时 Webhook_Delivery 由 DB FK CASCADE 自动清。
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在
    /// </remarks>
    /// <response code="200">删除成功</response>
    [HttpPost("delete")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteSub([FromBody] DeleteWebhookSubscriptionRequest req, CancellationToken ct)
    {
        await _subs.DeleteAsync(req, ct);
        return Ok(Wrap<object?>(null));
    }

    /// <summary>Test sub / 测试发送</summary>
    /// <remarks>
    /// Path：{id} 订阅 ID；body 空。
    /// 同步向 Subscription.Url 发送一次 test.ping 事件（与生产投递同形：HMAC + 4 个 X-PMM-* 头）。
    /// **不写 Webhook_Delivery 表**，避免污染生产投递历史。
    /// 成功响应：
    /// <code>
    /// { "code":0,"message":"ok","data":{"success":true,"httpStatus":200,"elapsedMilliseconds":124.3,"event":"test.ping","requestId":"...","errorMessage":null,"responseSnippet":"{...}"},"requestId":"..."}
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — Id 不存在 / Secret 解密失败
    /// </remarks>
    /// <response code="200">测试完成（success 字段标识 HTTP 链路是否成功）</response>
    [HttpPost("{id:long}/test")]
    [ProducesResponseType<ApiResponse<WebhookTestResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSub([FromRoute] long id, CancellationToken ct)
    {
        WebhookTestResult data = await _subs.TestAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Retry delivery / 重发投递</summary>
    /// <remarks>
    /// Path：{id} 订阅 ID、{deliveryId} 投递 ID；body 空。
    /// 将 Failed（或卡死的 Pending / Retrying）投递重置为 Pending、Attempts 归零并立即重新入队，
    /// 重新获得完整「首发 + 3 次退避重试（30s/2min/10min）」链。
    /// 成功响应：
    /// <code>
    /// { "code":0,"message":"ok","data":{"deliveryId":88,"status":"Pending","attempts":0},"requestId":"..."}
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — 订阅不存在 / 投递记录不存在 / 投递记录归属不匹配 / 投递记录已成功，无需重试
    /// - 9000 ServerError — 服务器内部错误（含重试入队失败）
    /// 错误响应：
    /// <code>
    /// { "code":1000,"message":"投递记录不存在","data":null,"requestId":"..."}
    /// </code>
    /// </remarks>
    /// <response code="200">重试已入队，返回重置后的投递状态</response>
    [HttpPost("{id:long}/retry/{deliveryId:long}")]
    [ProducesResponseType<ApiResponse<WebhookRetryResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryDelivery([FromRoute] long id, [FromRoute] long deliveryId, CancellationToken ct)
    {
        WebhookRetryResult data = await _deliveries.RetryAsync(id, deliveryId, ct);
        return Ok(Wrap(data));
    }

    /// <summary>List deliveries / 出站日志（分页）</summary>
    /// <remarks>
    /// Query：
    /// - subscriptionId（可选，long）：仅看某订阅
    /// - status（可选）：Pending / Retrying / Success / Failed
    /// - skip（默认 0）/ take（默认 50，上限 200）
    /// 成功响应：
    /// <code>
    /// { "code":0,"message":"ok","data":{"items":[{"id":1,"subscriptionId":1,"event":"media.archived","status":"Success","attempts":1,"lastTriedAt":"...","nextRetryAt":null,"lastStatusCode":200,"lastError":null,"requestId":"...","createdAt":"...","updatedAt":"..."}],"total":1},"requestId":"..."}
    /// </code>
    /// 错误码：
    /// - 1000 BusinessError — skip 为负（take ≤ 0 走默认 50；take &gt; 200 截断至 200；subscriptionId 不存在不报错，返回空 items）
    /// 错误响应：
    /// <code>
    /// { "code":1000,"message":"Skip 必须 ≥ 0","data":null,"requestId":"..."}
    /// </code>
    /// </remarks>
    /// <response code="200">日志分页</response>
    [HttpGet("deliveries")]
    [ProducesResponseType<ApiResponse<WebhookDeliveryPage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDeliveries(
        [FromQuery] long? subscriptionId,
        [FromQuery] WebhookDeliveryStatus? status,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        WebhookDeliveryPage data = await _deliveries.ListAsync(
            new WebhookDeliveryQuery(subscriptionId, status, skip, take), ct);
        return Ok(Wrap(data));
    }
}
