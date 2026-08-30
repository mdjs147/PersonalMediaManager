namespace PersonalMediaManager.Application.Contracts;

/// <summary>Webhook 测试发送契约</summary>
/// <remarks>
/// 实现在 Infrastructure.External 走 IHttpClientFactory；
/// 向给定 URL 发送一次 test.ping 事件（与生产投递同形：HMAC 签名 + X-PMM-* 头），
/// 不写 Webhook_Delivery 表（避免污染生产投递历史）；
/// Secret 由调用方（WebhookSubscriptionService）解密后传入明文。
/// </remarks>
public interface IWebhookTester
{
    Task<WebhookTestResult> SendAsync(
        string url,
        string? secret,
        int timeoutSeconds,
        CancellationToken ct = default);
}

/// <summary>测试发送结果</summary>
public sealed record WebhookTestResult(
    bool Success,
    int? HttpStatus,
    double ElapsedMilliseconds,
    string Event,
    string RequestId,
    string? ErrorMessage,
    string? ResponseSnippet);
