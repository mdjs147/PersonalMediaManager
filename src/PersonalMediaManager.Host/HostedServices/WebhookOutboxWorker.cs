using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.HostedServices;

/// <summary>WebhookOutboxWorker（D6.3）— 异步发送 Webhook + 失败退避 + HMAC 签名</summary>
/// <remarks>
/// 行为：从 IWebhookOutboxQueue 读 DeliveryId → 加载 Delivery + Subscription（独立 DbContext，§0.5 红线）
///   → 构造 POST 请求（headers + HMAC 签名） → 按 Subscription.TimeoutSeconds 发送 → 根据响应/异常更新 Delivery 状态。
///
/// 状态机：
///   2xx：Status=Success，LastStatusCode=N，NextRetryAt=null
///   失败（非 2xx 或异常）：Attempts++；
///     若 Attempts &lt; 4 → Status=Retrying，NextRetryAt = now + [30s, 2min, 10min][Attempts-1]
///     若 Attempts >= 4 → Status=Failed，NextRetryAt=null（首发 + 3 次重试全失败；可经手动重试端点重置救回）
///
/// 重试不在 Worker 内 sleep：只 persist NextRetryAt，由 WebhookRetryJob（D5.4）周期扫描并重新入队。
/// 这样进程重启不丢重试调度；Worker 自身保持「读队列 → 发一次 → 写状态」的简单不变式。
///
/// HMAC：sha256= + 小写十六进制；HMAC-SHA256(secret_utf8, body_bytes)；SecretEncrypted 为空则不发签名头。
/// 订阅被删 / Enabled=false：仍然按 Delivery 行尝试一次（订阅状态改变前的旧投递语义上应送达）；
///   订阅被物理删除 → SubscriptionId 找不到 → Delivery 直接 Failed（无 secret 不能签名 + 无 URL 不能发）。
///
/// 异常吞咽：单条失败仅写日志 + 更新状态，循环继续读下一条。
/// </remarks>
public sealed class WebhookOutboxWorker : BackgroundService
{
    /// <summary>命名 HttpClient 名（r2 P2-r2.3：与 Tmdb / Ai 等命名 client 风格统一，便于在 PmmHost 集中配置策略）</summary>
    public const string HttpClientName = "WebhookOutbox";

    private static readonly string UserAgent = $"PersonalMediaManager/{Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0"}";

    private readonly IWebhookOutboxQueue _queue;
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IProtectedFieldService _protector;
    private readonly IClock _clock;
    private readonly ILogger<WebhookOutboxWorker> _logger;

    public WebhookOutboxWorker(
        IWebhookOutboxQueue queue,
        IDbContextFactory<PmmDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        IProtectedFieldService protector,
        IClock clock,
        ILogger<WebhookOutboxWorker> logger)
    {
        _queue = queue;
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _protector = protector;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookOutboxWorker 启动");

        try
        {
            await foreach (long deliveryId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await SendOnceAsync(deliveryId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 取消信号必须穿透到外层 catch（不能被通配 Exception 吞掉），否则 Worker 退出循环失败
                    if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    _logger.LogError(ex, "Webhook 投递异常未捕获（DeliveryId={DeliveryId}）", deliveryId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关停
        }

        _logger.LogInformation("WebhookOutboxWorker 退出");
    }

    private async Task SendOnceAsync(long deliveryId, CancellationToken ct)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        WebhookDelivery? delivery = await db.WebhookDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null)
        {
            _logger.LogWarning("Delivery 已被清理：DeliveryId={DeliveryId}", deliveryId);
            return;
        }
        if (delivery.Status is WebhookDeliveryStatus.Success or WebhookDeliveryStatus.Failed)
        {
            _logger.LogDebug("Delivery 已终态，跳过：Id={Id}, Status={Status}", delivery.Id, delivery.Status);
            return;
        }

        WebhookSubscription? sub = await db.WebhookSubscriptions
            .FirstOrDefaultAsync(s => s.Id == delivery.SubscriptionId, ct);
        if (sub is null)
        {
            MarkFailed(delivery, statusCode: null, error: "订阅已被删除");
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Delivery 关联订阅已删除，标记 Failed：Id={Id}", delivery.Id);
            return;
        }

        (int? statusCode, string? error) = await TryPostAsync(sub, delivery, ct);
        UpdateAfterSend(delivery, statusCode, error);
        await db.SaveChangesAsync(ct);

        if (delivery.Status == WebhookDeliveryStatus.Success)
        {
            _logger.LogInformation("Webhook 发送成功：Id={Id}, Url={Url}, Status={Status}", delivery.Id, sub.Url, statusCode);
        }
        else
        {
            _logger.LogWarning(
                "Webhook 发送失败：Id={Id}, Url={Url}, Attempts={Attempts}, NextRetryAt={NextRetryAt}, Error={Error}",
                delivery.Id, sub.Url, delivery.Attempts, delivery.NextRetryAt, error ?? $"HTTP {statusCode}");
        }
    }

    private async Task<(int? statusCode, string? error)> TryPostAsync(WebhookSubscription sub, WebhookDelivery delivery, CancellationToken ct)
    {
        try
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(delivery.Payload);

            using HttpClient http = _httpFactory.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(Math.Max(1, sub.TimeoutSeconds));

            using HttpRequestMessage req = new(HttpMethod.Post, sub.Url);
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("X-PMM-Event", delivery.Event);
            req.Headers.TryAddWithoutValidation("X-PMM-Request-Id", delivery.RequestId);

            string? signature = ComputeSignature(sub.SecretEncrypted, bodyBytes);
            if (signature is not null)
            {
                req.Headers.TryAddWithoutValidation("X-PMM-Signature", signature);
            }

            using HttpResponseMessage resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            if (WebhookRetryPolicy.IsSuccess(code))
            {
                return (code, null);
            }
            string body = await SafeReadAsync(resp, ct);
            return (code, $"HTTP {code}: {Truncate(body, 500)}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient 超时（CancellationToken 没被外部触发）
            return (null, $"超时：{ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"网络错误：{ex.Message}");
        }
    }

    private string? ComputeSignature(string? secretEncrypted, byte[] body)
    {
        if (string.IsNullOrEmpty(secretEncrypted))
        {
            return null;
        }
        string secret;
        try
        {
            secret = _protector.Unprotect(secretEncrypted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook Secret 解密失败，本次跳过签名");
            return null;
        }
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secret));
        byte[] hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void UpdateAfterSend(WebhookDelivery delivery, int? statusCode, string? error)
    {
        DateTimeOffset now = _clock.UtcNow;
        delivery.Attempts++;
        delivery.LastTriedAt = now;
        delivery.LastStatusCode = statusCode;
        delivery.LastError = error;

        if (statusCode.HasValue && WebhookRetryPolicy.IsSuccess(statusCode.Value))
        {
            delivery.Status = WebhookDeliveryStatus.Success;
            delivery.NextRetryAt = null;
            delivery.LastError = null;
            return;
        }

        TimeSpan? next = WebhookRetryPolicy.NextRetryAfter(delivery.Attempts);
        if (next is null)
        {
            delivery.Status = WebhookDeliveryStatus.Failed;
            delivery.NextRetryAt = null;
        }
        else
        {
            delivery.Status = WebhookDeliveryStatus.Retrying;
            delivery.NextRetryAt = now + next.Value;
        }
    }

    private static void MarkFailed(WebhookDelivery delivery, int? statusCode, string error)
    {
        delivery.Status = WebhookDeliveryStatus.Failed;
        delivery.Attempts++;
        delivery.LastStatusCode = statusCode;
        delivery.LastError = error;
        delivery.NextRetryAt = null;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
