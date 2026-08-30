using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>WebhookOutboxWorker（D6.3）— HMAC 签名 + 退避状态机 + DB 转状态</summary>
/// <remarks>
/// 用 SQLite in-memory 跑真实 DbContext + 注入 RecordingHttpHandler 控制 HTTP 响应，
/// Worker 自己运行 ExecuteAsync 循环，测试通过 EnqueueAsync 推送 + WaitUntil DB 状态变迁来断言。
/// 退避语义（需求文档 §3.10）：首发 1 + 重试 3（30s/2min/10min）= 4 次尝试全失败才 Failed。
/// </remarks>
public sealed class WebhookOutboxWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly FakeProtector _protector = new();
    private readonly FakeClock _clock = new();

    public WebhookOutboxWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---------- WebhookRetryPolicy 纯函数 ----------

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    public void RetryPolicy_NextRetryAfter_Returns_Expected_Interval(int failedAttempts, int expectedSeconds)
    {
        TimeSpan? next = WebhookRetryPolicy.NextRetryAfter(failedAttempts);
        next.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    public void RetryPolicy_NextRetryAfter_AtOrPastLimit_Returns_Null(int failedAttempts)
    {
        WebhookRetryPolicy.NextRetryAfter(failedAttempts).Should().BeNull();
    }

    [Fact]
    public void RetryPolicy_MaxAttempts_Is_FirstSend_Plus_Three_Retries()
    {
        // 需求文档 §3.10：「退避重试 3 次，间隔 30s/2min/10min」→ 含首发共 4 次尝试
        WebhookRetryPolicy.MaxAttempts.Should().Be(4);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(299, true)]
    [InlineData(199, false)]
    [InlineData(300, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    public void RetryPolicy_IsSuccess(int code, bool expected)
    {
        WebhookRetryPolicy.IsSuccess(code).Should().Be(expected);
    }

    // ---------- Worker 行为 ----------

    [Fact]
    public async Task Send_200_Marks_Delivery_Success()
    {
        long subId = SeedSubscription(url: "https://hook/ok");
        long deliveryId = SeedDelivery(subId, payload: "{\"a\":1}");
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.OK);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Success);
        await sut.StopAsync(CancellationToken.None);

        WebhookDelivery d = ReadDelivery(deliveryId);
        d.Attempts.Should().Be(1);
        d.LastStatusCode.Should().Be(200);
        d.LastError.Should().BeNull();
        d.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Send_500_Marks_Retrying_With_30s_NextRetryAt()
    {
        long subId = SeedSubscription(url: "https://hook/500");
        long deliveryId = SeedDelivery(subId, payload: "{}");
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        _clock.Set(now);
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.InternalServerError, body: "boom");
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Retrying);
        await sut.StopAsync(CancellationToken.None);

        WebhookDelivery d = ReadDelivery(deliveryId);
        d.Attempts.Should().Be(1);
        d.LastStatusCode.Should().Be(500);
        d.LastError.Should().Contain("HTTP 500");
        d.NextRetryAt.Should().Be(now + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Third_Failure_Schedules_10min_Third_Tier_Retry()
    {
        // 已失败 2 次（Retrying 态），第 3 次仍失败 → 进入第三档退避 10min，而非直接 Failed
        long subId = SeedSubscription(url: "https://hook/tier3");
        long deliveryId = SeedDeliveryWithAttempts(subId, attempts: 2);
        DateTimeOffset now = new(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        _clock.Set(now);
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.BadGateway);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilDelivery(deliveryId, d => d.Attempts == 3);
        await sut.StopAsync(CancellationToken.None);

        WebhookDelivery d = ReadDelivery(deliveryId);
        d.Status.Should().Be(WebhookDeliveryStatus.Retrying);
        d.NextRetryAt.Should().Be(now + TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Fourth_Failure_Exhausts_Chain_Marks_Failed_No_Retry()
    {
        // 首发 + 2 次重试已失败（attempts=3），第 4 次（最后一次重试）仍失败 → 链耗尽，Failed
        long subId = SeedSubscription(url: "https://hook/fail");
        long deliveryId = SeedDeliveryWithAttempts(subId, attempts: 3);
        _clock.Set(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.BadGateway);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Failed);
        await sut.StopAsync(CancellationToken.None);

        WebhookDelivery d = ReadDelivery(deliveryId);
        d.Attempts.Should().Be(4);
        d.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task HMAC_Signature_Header_Is_Correct_For_Body()
    {
        const string secret = "s3cret";
        const string body = "{\"event\":\"media.archived\"}";
        long subId = SeedSubscription(url: "https://hook/sig", secretPlain: secret);
        long deliveryId = SeedDelivery(subId, payload: body);
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.OK);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Success);
        await sut.StopAsync(CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.TryGetValues("X-PMM-Signature", out IEnumerable<string>? sigs).Should().BeTrue();
        string expected = "sha256=" + ToHex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        sigs!.Single().Should().Be(expected);
        handler.LastRequest.Headers.GetValues("X-PMM-Event").Single().Should().Be("media.archived");
    }

    [Fact]
    public async Task No_Secret_Skips_Signature_Header_But_Still_Sends()
    {
        long subId = SeedSubscription(url: "https://hook/nosig", secretPlain: null);
        long deliveryId = SeedDelivery(subId, payload: "{}");
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.OK);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Success);
        await sut.StopAsync(CancellationToken.None);

        handler.LastRequest!.Headers.Contains("X-PMM-Signature").Should().BeFalse(
            "无 Secret 应跳过签名头但仍发送请求");
    }

    [Fact]
    public async Task Network_Exception_Treated_As_Failure()
    {
        long subId = SeedSubscription(url: "https://hook/down");
        long deliveryId = SeedDelivery(subId, payload: "{}");
        _clock.Set(new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero));
        RecordingHttpHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Retrying);
        await sut.StopAsync(CancellationToken.None);

        WebhookDelivery d = ReadDelivery(deliveryId);
        d.LastStatusCode.Should().BeNull();
        d.LastError.Should().Contain("网络错误").And.Contain("connection refused");
    }

    [Fact]
    public async Task Missing_Subscription_Marks_Failed()
    {
        // 先正常 seed sub + delivery，再绕过 FK CASCADE 单独删 sub，模拟「订阅被删但 delivery 残留」场景
        long subId = SeedSubscription(url: "https://hook/will-die");
        long deliveryId = SeedDelivery(subId, payload: "{}");
        DeleteSubscriptionBypassingFk(subId);

        RecordingHttpHandler handler = RespondWith(HttpStatusCode.OK);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);

        await WaitUntilStatus(deliveryId, WebhookDeliveryStatus.Failed);
        await sut.StopAsync(CancellationToken.None);

        handler.LastRequest.Should().BeNull("订阅不存在不应发起 HTTP");
        WebhookDelivery d = ReadDelivery(deliveryId);
        d.LastError.Should().Be("订阅已被删除");
    }

    private void DeleteSubscriptionBypassingFk(long subId)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=OFF; DELETE FROM Webhook_Subscription WHERE Id=$id; PRAGMA foreign_keys=ON;";
        cmd.Parameters.AddWithValue("$id", subId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Already_Success_Delivery_Is_Skipped_NoSend()
    {
        long subId = SeedSubscription(url: "https://hook/skip");
        long deliveryId = SeedDeliveryWithStatus(subId, WebhookDeliveryStatus.Success);
        RecordingHttpHandler handler = RespondWith(HttpStatusCode.OK);
        WebhookOutboxQueue queue = new();

        WebhookOutboxWorker sut = NewWorker(queue, handler);
        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(deliveryId);
        await Task.Delay(150);
        await sut.StopAsync(CancellationToken.None);

        handler.LastRequest.Should().BeNull("终态 Delivery 必须跳过，避免重复发送");
    }

    // ---------- helpers ----------

    private WebhookOutboxWorker NewWorker(IWebhookOutboxQueue queue, HttpMessageHandler handler)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new WebhookOutboxWorker(
            queue, _dbFactory, factory, _protector, _clock, NullLogger<WebhookOutboxWorker>.Instance);
    }

    private long SeedSubscription(string url, string? secretPlain = "default-secret")
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        WebhookSubscription s = new()
        {
            Name = $"sub-{Guid.NewGuid():N}",
            Url = url,
            SecretEncrypted = secretPlain is null ? null : _protector.Protect(secretPlain),
            Events = ["media.archived"],
            Enabled = true,
            TimeoutSeconds = 10,
        };
        ctx.WebhookSubscriptions.Add(s);
        ctx.SaveChanges();
        return s.Id;
    }

    private long SeedDelivery(long subscriptionId, string payload)
        => SeedDeliveryWithAttempts(subscriptionId, attempts: 0, payload: payload);

    private long SeedDeliveryWithAttempts(long subscriptionId, int attempts, string payload = "{}")
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        WebhookDelivery d = new()
        {
            SubscriptionId = subscriptionId,
            Event = "media.archived",
            Payload = payload,
            Status = attempts == 0 ? WebhookDeliveryStatus.Pending : WebhookDeliveryStatus.Retrying,
            Attempts = attempts,
            RequestId = Guid.NewGuid().ToString("N"),
        };
        ctx.WebhookDeliveries.Add(d);
        ctx.SaveChanges();
        return d.Id;
    }

    private long SeedDeliveryWithStatus(long subscriptionId, WebhookDeliveryStatus status)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        WebhookDelivery d = new()
        {
            SubscriptionId = subscriptionId,
            Event = "media.archived",
            Payload = "{}",
            Status = status,
            Attempts = 1,
            RequestId = Guid.NewGuid().ToString("N"),
        };
        ctx.WebhookDeliveries.Add(d);
        ctx.SaveChanges();
        return d.Id;
    }

    private WebhookDelivery ReadDelivery(long id)
    {
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        return ctx.WebhookDeliveries.AsNoTracking().Single(d => d.Id == id);
    }

    private Task WaitUntilStatus(long deliveryId, WebhookDeliveryStatus expected, int timeoutMs = 20_000)
        => WaitUntilDelivery(deliveryId, d => d.Status == expected, timeoutMs, $"转到 {expected}");

    private async Task WaitUntilDelivery(long deliveryId, Func<WebhookDelivery, bool> predicate, int timeoutMs = 20_000, string? what = null)
    {
        // 默认 20s：单测 happy path 仍 < 100ms 完成（每 25ms 轮询一次，命中后立即返回），
        // 但与全套测试并行跑时 DI scope / DbContext / Thread pool 资源争抢偶发让 outbox worker 处理慢，
        // 旧默认 10s 留下两条记录在案的偶发 timing flake（Send_200 / No_Secret），20s 留足缓冲不影响单测速度。
        // 按谓词等待：种子初始态可能已等于目标 Status（如 Retrying → Retrying 第三档），此时改等 Attempts 变迁。
        int waited = 0;
        while (waited < timeoutMs)
        {
            using PmmDbContext ctx = _dbFactory.CreateDbContext();
            WebhookDelivery? d = ctx.WebhookDeliveries.AsNoTracking().FirstOrDefault(x => x.Id == deliveryId);
            if (d is not null && predicate(d)) return;
            await Task.Delay(25);
            waited += 25;
        }
        throw new TimeoutException($"Delivery {deliveryId} 未在 {timeoutMs}ms 内{what ?? "满足等待条件"}");
    }

    private static RecordingHttpHandler RespondWith(HttpStatusCode code, string body = "")
        => new(_ => new HttpResponseMessage(code) { Content = new StringContent(body) });

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }

    /// <summary>反转编码即加密的假实现，足够还原 Protect → Unprotect 往返；不引 DataProtection 真实依赖</summary>
    private sealed class FakeProtector : IProtectedFieldService
    {
        public string Protect(string plaintext)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext)
            => Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }

    private sealed class FakeClock : IClock
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Set(DateTimeOffset t) => _now = t;
        public DateTimeOffset UtcNow => _now;
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 克隆头部 + content 让请求消亡后断言仍可用
            HttpRequestMessage clone = new(request.Method, request.RequestUri);
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(name, values);
            }
            if (request.Content is not null)
            {
                byte[] bytes = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                clone.Content = new ByteArrayContent(bytes);
                foreach ((string name, IEnumerable<string> values) in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(name, values);
                }
            }
            LastRequest = clone;
            return Task.FromResult(_responder(request));
        }
    }
}
