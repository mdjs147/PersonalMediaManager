using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>Webhooks e2e：订阅 CRUD + Secret 加密 + Events 空 1000 + 出站日志分页 / 状态过滤 / CASCADE + 手动重试</summary>
public sealed class WebhooksTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public WebhooksTests()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        _factory = new PmmHostFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SetupGuardMiddleware.ResetCacheForTest();
    }

    [Fact]
    public async Task Sub_Crud_RoundTrip_SecretEncrypted()
    {
        await LoginAsAdminAsync();
        const string secret = "hmac-shared-secret";

        JsonElement created = await PostAsync("/api/settings/webhooks", new
        {
            name = "plex",
            url = "https://hooks.example.com/plex",
            secret,
            events = new[] { "media.archived", "media.failed" },
            enabled = true,
            timeoutSeconds = 10,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        created.GetProperty("data").GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        created.GetRawText().Should().NotContain(secret, "Secret 明文不能出现在响应里");

        // DB 列加密
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            WebhookSubscription sub = await ctx.WebhookSubscriptions.AsNoTracking().FirstAsync(s => s.Id == id);
            sub.SecretEncrypted.Should().NotBe(secret, "DB 列必须是密文");
            sub.Events.Should().BeEquivalentTo(["media.archived", "media.failed"]);
        }

        // Update：secret=null 保留密钥，仅改 enabled
        JsonElement upd = await PostAsync("/api/settings/webhooks/update", new
        {
            id,
            name = "plex",
            url = "https://hooks.example.com/plex2",
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = false,
            timeoutSeconds = 5,
        });
        upd.GetProperty("data").GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        upd.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse();

        JsonElement del = await PostAsync("/api/settings/webhooks/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task Create_EmptyEvents_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/webhooks", new
        {
            name = "no-events",
            url = "https://example.com",
            secret = (string?)null,
            events = Array.Empty<string>(),
            enabled = true,
            timeoutSeconds = 10,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("Events");
    }

    [Fact]
    public async Task Create_UnknownEvent_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/webhooks", new
        {
            name = "bad-event",
            url = "https://example.com",
            secret = (string?)null,
            events = new[] { "bogus.event" },
            enabled = true,
            timeoutSeconds = 10,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("未知事件");
    }

    [Fact]
    public async Task GlobalSwitch_DefaultOff_Toggle_RoundTrip()
    {
        await LoginAsAdminAsync();

        // 种子默认关闭
        JsonElement off = await GetAsync("/api/settings/webhooks/enabled");
        off.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        off.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse("种子默认 Webhook_Enabled=false");

        // 打开 → 返回设置后的最新状态
        JsonElement set = await PostAsync("/api/settings/webhooks/enabled", new { enabled = true });
        set.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeTrue();

        // 再读应持久化为 true
        JsonElement on = await GetAsync("/api/settings/webhooks/enabled");
        on.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deliveries_PagingAndStatusFilter()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/webhooks", new
        {
            name = "with-deliveries",
            url = "https://example.com",
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = true,
            timeoutSeconds = 10,
        });
        long subId = created.GetProperty("data").GetProperty("id").GetInt64();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            for (int i = 0; i < 5; i++)
            {
                ctx.WebhookDeliveries.Add(new WebhookDelivery
                {
                    SubscriptionId = subId,
                    Event = "media.archived",
                    Payload = "{}",
                    Status = i % 2 == 0 ? WebhookDeliveryStatus.Success : WebhookDeliveryStatus.Failed,
                    Attempts = 1,
                    LastTriedAt = DateTimeOffset.UtcNow,
                    LastStatusCode = i % 2 == 0 ? 200 : 500,
                    RequestId = $"req-{i}",
                });
            }
            await ctx.SaveChangesAsync();
        }

        // 全部：5 条
        JsonElement all = await GetAsync($"/api/settings/webhooks/deliveries?subscriptionId={subId}");
        all.GetProperty("data").GetProperty("total").GetInt32().Should().Be(5);
        all.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(5);

        // 只看 Failed：2 条
        JsonElement onlyFailed = await GetAsync($"/api/settings/webhooks/deliveries?subscriptionId={subId}&status=Failed");
        onlyFailed.GetProperty("data").GetProperty("total").GetInt32().Should().Be(2);
        foreach (JsonElement item in onlyFailed.GetProperty("data").GetProperty("items").EnumerateArray())
            item.GetProperty("status").GetString().Should().Be("Failed");

        // 分页：take=2
        JsonElement page = await GetAsync($"/api/settings/webhooks/deliveries?subscriptionId={subId}&skip=0&take=2");
        page.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("data").GetProperty("total").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task List_Subs_Aggregates_DeliveredAndFailedCounts()
    {
        await LoginAsAdminAsync();
        // 订阅 A：1 Success + 2 Failed + 1 Pending
        long subA = (await PostAsync("/api/settings/webhooks", new
        {
            name = "agg-A",
            url = "https://example.com/a",
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = true,
            timeoutSeconds = 10,
        })).GetProperty("data").GetProperty("id").GetInt64();
        // 订阅 B：0 投递
        long subB = (await PostAsync("/api/settings/webhooks", new
        {
            name = "agg-B",
            url = "https://example.com/b",
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = true,
            timeoutSeconds = 10,
        })).GetProperty("data").GetProperty("id").GetInt64();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ctx.WebhookDeliveries.AddRange(
                new WebhookDelivery { SubscriptionId = subA, Event = "media.archived", Payload = "{}", Status = WebhookDeliveryStatus.Success, Attempts = 1, RequestId = "r1" },
                new WebhookDelivery { SubscriptionId = subA, Event = "media.archived", Payload = "{}", Status = WebhookDeliveryStatus.Failed, Attempts = 3, RequestId = "r2" },
                new WebhookDelivery { SubscriptionId = subA, Event = "media.archived", Payload = "{}", Status = WebhookDeliveryStatus.Failed, Attempts = 3, RequestId = "r3" },
                new WebhookDelivery { SubscriptionId = subA, Event = "media.archived", Payload = "{}", Status = WebhookDeliveryStatus.Pending, Attempts = 0, RequestId = "r4" }
            );
            await ctx.SaveChangesAsync();
        }

        JsonElement list = await GetAsync("/api/settings/webhooks");
        list.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement[] subs = list.GetProperty("data").EnumerateArray().ToArray();
        JsonElement a = subs.Single(s => s.GetProperty("id").GetInt64() == subA);
        JsonElement b = subs.Single(s => s.GetProperty("id").GetInt64() == subB);
        a.GetProperty("deliveredCount").GetInt32().Should().Be(1, "1 Success");
        a.GetProperty("failedCount").GetInt32().Should().Be(2, "2 Failed (Pending 不计)");
        b.GetProperty("deliveredCount").GetInt32().Should().Be(0);
        b.GetProperty("failedCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task TestSub_IdNotFound_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/webhooks/99999/test", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("99999");
    }

    [Fact]
    public async Task TestSub_UnreachableUrl_ReturnsSuccessFalse()
    {
        await LoginAsAdminAsync();
        // 端口 1：保留端口，几乎不可能监听 → 立即 ECONNREFUSED
        JsonElement created = await PostAsync("/api/settings/webhooks", new
        {
            name = "test-unreachable",
            url = "http://127.0.0.1:1/webhook",
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = true,
            timeoutSeconds = 1,
        });
        long subId = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement resp = await PostAsync($"/api/settings/webhooks/{subId}/test", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success, "端点本身返回 200，业务结果在 data 里");
        JsonElement data = resp.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeFalse();
        data.GetProperty("event").GetString().Should().Be("test.ping");
        data.GetProperty("errorMessage").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("requestId").GetString().Should().NotBeNullOrEmpty();
        // 不入 Delivery 表
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext ctx = await dbFactory.CreateDbContextAsync();
        int rows = await ctx.WebhookDeliveries.CountAsync(d => d.SubscriptionId == subId);
        rows.Should().Be(0, "测试发送不应入 Webhook_Delivery 表");
    }

    [Fact]
    public async Task DeleteSub_CascadesDeliveries()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/webhooks", new
        {
            name = "to-cascade",
            url = "https://example.com",
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = true,
            timeoutSeconds = 10,
        });
        long subId = created.GetProperty("data").GetProperty("id").GetInt64();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ctx.WebhookDeliveries.Add(new WebhookDelivery
            {
                SubscriptionId = subId,
                Event = "media.archived",
                Payload = "{}",
                Status = WebhookDeliveryStatus.Success,
                RequestId = "rid",
            });
            await ctx.SaveChangesAsync();
        }

        JsonElement del = await PostAsync("/api/settings/webhooks/delete", new { id = subId });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            int remaining = await ctx.WebhookDeliveries.CountAsync(d => d.SubscriptionId == subId);
            remaining.Should().Be(0, "FK CASCADE 应自动清理出站日志");
        }
    }

    // ---------- 手动重试（API 规范 §2.16.6） ----------

    [Fact]
    public async Task RetryDelivery_Failed_ResetsToPending_AttemptsZero()
    {
        await LoginAsAdminAsync();
        // 端口 1：保留端口几乎不可能监听 → OutboxWorker 拾起后立即 ECONNREFUSED，不依赖外网
        long subId = await CreateSubAsync("retry-target", "http://127.0.0.1:1/webhook");
        long deliveryId = await SeedDeliveryAsync(subId, WebhookDeliveryStatus.Failed, attempts: 4);

        JsonElement resp = await PostAsync($"/api/settings/webhooks/{subId}/retry/{deliveryId}", new { });

        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement data = resp.GetProperty("data");
        data.GetProperty("deliveryId").GetInt64().Should().Be(deliveryId);
        data.GetProperty("status").GetString().Should().Be("Pending", "重置后回 Pending 立即可重试");
        data.GetProperty("attempts").GetInt32().Should().Be(0, "Attempts 归零，重新获得完整退避重试链");
        // 不回读 DB 断言：响应返回后 OutboxWorker 可能已拾起并把状态推进到 Retrying，回读会与 Worker 竞态
    }

    [Fact]
    public async Task RetryDelivery_SubNotFound_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/webhooks/99999/retry/1", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Be("订阅不存在");
    }

    [Fact]
    public async Task RetryDelivery_DeliveryNotFound_Returns1000()
    {
        await LoginAsAdminAsync();
        long subId = await CreateSubAsync("retry-no-delivery", "https://example.com");
        JsonElement resp = await PostAsync($"/api/settings/webhooks/{subId}/retry/99999", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Be("投递记录不存在");
    }

    [Fact]
    public async Task RetryDelivery_WrongOwner_Returns1000()
    {
        await LoginAsAdminAsync();
        long subA = await CreateSubAsync("retry-owner-a", "https://example.com/a");
        long subB = await CreateSubAsync("retry-owner-b", "https://example.com/b");
        long deliveryOfB = await SeedDeliveryAsync(subB, WebhookDeliveryStatus.Failed, attempts: 4);

        JsonElement resp = await PostAsync($"/api/settings/webhooks/{subA}/retry/{deliveryOfB}", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Be("投递记录归属不匹配");
    }

    [Fact]
    public async Task RetryDelivery_AlreadySuccess_Returns1000()
    {
        await LoginAsAdminAsync();
        long subId = await CreateSubAsync("retry-success", "https://example.com");
        long deliveryId = await SeedDeliveryAsync(subId, WebhookDeliveryStatus.Success, attempts: 1);

        JsonElement resp = await PostAsync($"/api/settings/webhooks/{subId}/retry/{deliveryId}", new { });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Be("投递记录已成功，无需重试");
    }

    private async Task<long> CreateSubAsync(string name, string url)
    {
        JsonElement created = await PostAsync("/api/settings/webhooks", new
        {
            name,
            url,
            secret = (string?)null,
            events = new[] { "media.archived" },
            enabled = true,
            timeoutSeconds = 1,
        });
        return created.GetProperty("data").GetProperty("id").GetInt64();
    }

    private async Task<long> SeedDeliveryAsync(long subId, WebhookDeliveryStatus status, int attempts)
    {
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using PmmDbContext ctx = await dbFactory.CreateDbContextAsync();
        WebhookDelivery d = new()
        {
            SubscriptionId = subId,
            Event = "media.archived",
            Payload = "{}",
            Status = status,
            Attempts = attempts,
            LastStatusCode = status == WebhookDeliveryStatus.Success ? 200 : 502,
            LastError = status == WebhookDeliveryStatus.Success ? null : "HTTP 502: bad gateway",
            RequestId = Guid.NewGuid().ToString("N"),
        };
        ctx.WebhookDeliveries.Add(d);
        await ctx.SaveChangesAsync();
        return d.Id;
    }

    private async Task LoginAsAdminAsync()
    {
        await PostAsync("/api/setup/admin", new { username = "admin", password = "secret123" });
        await PostAsync("/api/setup/complete", new { });
        JsonElement login = await PostAsync("/api/auth/login", new { username = "admin", password = "secret123" });
        string token = login.GetProperty("data").GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<JsonElement> GetAsync(string url) =>
        await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> PostAsync(string url, object payload) =>
        await (await _client.PostAsJsonAsync(url, payload)).Content.ReadFromJsonAsync<JsonElement>();
}
