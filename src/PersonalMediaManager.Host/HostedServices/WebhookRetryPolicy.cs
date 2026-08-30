namespace PersonalMediaManager.Host.HostedServices;

/// <summary>Webhook 退避策略（需求文档 §3.10）</summary>
/// <remarks>
/// 需求语义：「失败 → 退避重试 3 次，间隔 30s / 2min / 10min；3 次（重试）全失败 → 标记 Failed」。
/// 即 MaxAttempts = 首发 1 次 + 退避重试 3 次 = 4 次尝试：
///   第 1 次失败 → 等 30s 重试；第 2 次 → 2min；第 3 次 → 10min；第 4 次仍失败 → 不再调度，标记 Failed
///   （此后仅可经手动重试端点 POST settings/webhooks/{id}/retry/{deliveryId} 重置救回）。
/// 静态纯函数：测试可直连，与 HttpClient / DbContext 解耦。
/// </remarks>
public static class WebhookRetryPolicy
{
    /// <summary>尝试上限（含首次发送）：首发 1 + 退避重试 3 = 4</summary>
    public const int MaxAttempts = 4;

    /// <summary>给定「已尝试失败次数」返回下一次重试的延迟；返回 null 表示已达上限不再重试</summary>
    /// <param name="failedAttempts">已失败的尝试次数（首次失败后传 1，第二次失败后传 2）</param>
    public static TimeSpan? NextRetryAfter(int failedAttempts) => failedAttempts switch
    {
        1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => null, // >=4 已达上限（首发 + 3 次重试全失败）
    };

    /// <summary>HTTP 状态码是否视为业务成功（2xx）</summary>
    public static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;
}
