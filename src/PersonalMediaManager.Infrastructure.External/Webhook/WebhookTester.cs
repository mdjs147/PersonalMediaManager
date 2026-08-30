using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Webhook;

/// <summary>Webhook 测试发送实现（C.7 /test 端点使用）</summary>
/// <remarks>
/// 发送同步一次 test.ping 事件 —— 与 WebhookOutboxWorker 生产投递同形：
/// - 头部：User-Agent / X-PMM-Event=test.ping / X-PMM-Request-Id / X-PMM-Signature=sha256=...
/// - Body：{"event":"test.ping","timestamp":"ISO8601","requestId":"...","data":{"message":"..."}}
/// - HMAC：SecretEncrypted→明文 由调用方解密；本类不碰 IProtectedFieldService。
/// - 超时：调用方传入的 timeoutSeconds（与 Subscription.TimeoutSeconds 一致），夹紧 [1,60]。
/// 不入 Webhook_Delivery 表；任何异常归集到 Success=false + ErrorMessage。
/// </remarks>
internal sealed class WebhookTester : IWebhookTester
{
    private static readonly string UserAgent =
        $"PersonalMediaManager/{Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0"}";

    private const string TestEvent = "test.ping";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebhookTester> _logger;

    public WebhookTester(IHttpClientFactory httpFactory, ILogger<WebhookTester> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<WebhookTestResult> SendAsync(
        string url,
        string? secret,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        string requestId = Guid.NewGuid().ToString("N");

        if (string.IsNullOrWhiteSpace(url))
            return new WebhookTestResult(false, null, 0, TestEvent, requestId, "Url 不能为空", null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new WebhookTestResult(false, null, 0, TestEvent, requestId, "Url 必须是合法的 http / https URL", null);

        byte[] bodyBytes = BuildPayload(requestId);

        HttpClient client = _httpFactory.CreateClient("WebhookTester");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));

        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("X-PMM-Event", TestEvent);
        req.Headers.TryAddWithoutValidation("X-PMM-Request-Id", requestId);

        string? signature = ComputeSignature(secret, bodyBytes);
        if (signature is not null)
            req.Headers.TryAddWithoutValidation("X-PMM-Signature", signature);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            int code = (int)resp.StatusCode;
            string body = await SafeReadAsync(resp, ct);
            string? snippet = body.Length > 500 ? body[..500] : body;

            bool ok = resp.IsSuccessStatusCode;
            return new WebhookTestResult(
                ok,
                code,
                sw.Elapsed.TotalMilliseconds,
                TestEvent,
                requestId,
                ok ? null : $"HTTP {code} {resp.ReasonPhrase}",
                snippet);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new WebhookTestResult(false, null, sw.Elapsed.TotalMilliseconds, TestEvent, requestId,
                $"超时（{timeoutSeconds}s）：{ex.Message}", null);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Webhook 测试发送网络异常 {Url}", url);
            return new WebhookTestResult(false, null, sw.Elapsed.TotalMilliseconds, TestEvent, requestId,
                $"网络错误：{ex.Message}", null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Webhook 测试发送未预期异常 {Url}", url);
            return new WebhookTestResult(false, null, sw.Elapsed.TotalMilliseconds, TestEvent, requestId,
                ex.Message, null);
        }
    }

    private static byte[] BuildPayload(string requestId)
    {
        var payload = new
        {
            @event = TestEvent,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            requestId,
            data = new
            {
                message = "PersonalMediaManager test webhook",
                hint = "若收到此事件，说明 URL + HMAC + 网络链路均正常",
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            // 与 OutboxWorker 一致：默认 camelCase off（保留 PascalCase 由匿名对象字段名直出）；
            // 此处显式声明以便后续切换 Naming 时统一调整。
        });
    }

    private static string? ComputeSignature(string? secret, byte[] body)
    {
        if (string.IsNullOrEmpty(secret))
            return null;
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secret));
        byte[] hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }
}
