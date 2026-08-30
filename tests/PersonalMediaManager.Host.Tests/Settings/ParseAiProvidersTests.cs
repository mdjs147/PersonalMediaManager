using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Settings;

/// <summary>ParseAiProviders e2e：CRUD + ApiKey 加密 + IsPrimary 唯一 + /test 不可达回 success=false + /enable 清 DisabledUntil</summary>
public sealed class ParseAiProvidersTests : IDisposable
{
    private readonly PmmHostFactory _factory;
    private readonly HttpClient _client;

    public ParseAiProvidersTests()
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
    public async Task Crud_RoundTrip_ApiKeyEncryptedAndNotReturned()
    {
        await LoginAsAdminAsync();

        const string apiKey = "sk-secret-123";
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "deepseek",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey,
            model = "deepseek-chat",
            isPrimary = true,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        long id = created.GetProperty("data").GetProperty("id").GetInt64();
        created.GetProperty("data").GetProperty("hasApiKey").GetBoolean().Should().BeTrue();
        // 响应不暴露密文 / 明文
        string raw = created.GetRawText();
        raw.Should().NotContain(apiKey, "ApiKey 明文不能出现在响应里");

        // 直接读 DB 验证加密
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ParseAiProvider e = await ctx.ParseAiProviders.AsNoTracking().FirstAsync(p => p.Id == id);
            e.ApiKeyEncrypted.Should().NotBeNull();
            e.ApiKeyEncrypted.Should().NotBe(apiKey, "DB 列必须是密文不是明文");
        }

        // Update：apiKey=null 表示不改，仅改 model
        JsonElement upd = await PostAsync("/api/settings/ai-providers/update", new
        {
            id,
            name = "deepseek",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat-v2",
            isPrimary = true,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        upd.GetProperty("data").GetProperty("model").GetString().Should().Be("deepseek-chat-v2");
        upd.GetProperty("data").GetProperty("hasApiKey").GetBoolean().Should().BeTrue();

        JsonElement del = await PostAsync("/api/settings/ai-providers/delete", new { id });
        del.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
    }

    [Fact]
    public async Task Create_CloudWithoutApiKey_Succeeds_ApiKeyOptional()
    {
        // 协议化重构后 ApiKey 统一可选（支持本地 OpenAI 兼容服务 / 免鉴权反代等场景）：缺 Key 不再拒绝，
        // 是否需要鉴权交由「测试连接 / 真实调用」时的 401 暴露。
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/ai-providers", new
        {
            name = "openai-no-key",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat",
            isPrimary = false,
            priority = 200,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        resp.GetProperty("data").GetProperty("hasApiKey").GetBoolean().Should().BeFalse("未提供 Key → hasApiKey=false");
    }

    [Fact]
    public async Task Create_InvalidBaseUrl_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/ai-providers", new
        {
            name = "bad",
            type = "Ollama",
            baseUrl = "not-a-url",
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("URL");
    }

    [Fact]
    public async Task IsPrimary_IsUnique_AcrossTable()
    {
        await LoginAsAdminAsync();
        await PostAsync("/api/settings/ai-providers", new
        {
            name = "first-primary",
            type = "Ollama",
            baseUrl = "http://127.0.0.1:11434",
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = true,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        await PostAsync("/api/settings/ai-providers", new
        {
            name = "second-primary",
            type = "Ollama",
            baseUrl = "http://127.0.0.1:11434",
            apiKey = (string?)null,
            model = "qwen",
            isPrimary = true,
            priority = 50,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });

        JsonElement list = await GetAsync("/api/settings/ai-providers");
        int primaryCount = list.GetProperty("data").EnumerateArray()
            .Count(e => e.GetProperty("isPrimary").GetBoolean());
        primaryCount.Should().Be(1, "全表至多一个 IsPrimary=true，第二条创建时旧主自动降级");

        string primaryName = list.GetProperty("data").EnumerateArray()
            .First(e => e.GetProperty("isPrimary").GetBoolean())
            .GetProperty("name").GetString()!;
        primaryName.Should().Be("second-primary");
    }

    [Fact]
    public async Task Test_UnreachableEndpoint_ReturnsSuccessFalse()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "unreachable",
            type = "Ollama",
            baseUrl = "http://127.0.0.1:1", // 1 端口必然不可达
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 2,
            extraOptions = (string?)null,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        JsonElement test = await PostAsync("/api/settings/ai-providers/test", new { id });
        test.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        test.GetProperty("data").GetProperty("success").GetBoolean().Should().BeFalse();
        test.GetProperty("data").GetProperty("errorMessage").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Enable_ClearsDisabledUntil()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "to-enable",
            type = "Ollama",
            baseUrl = "http://127.0.0.1:11434",
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        // 直接 DB 写入 DisabledUntil（模拟 D 阶段健康追踪写入）
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ParseAiProvider e = await ctx.ParseAiProviders.FirstAsync(p => p.Id == id);
            e.DisabledUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            await ctx.SaveChangesAsync();
        }

        // /enable 后应被清空
        JsonElement enable = await PostAsync("/api/settings/ai-providers/enable", new { id });
        enable.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ParseAiProvider e = await ctx.ParseAiProviders.AsNoTracking().FirstAsync(p => p.Id == id);
            e.DisabledUntil.Should().BeNull("/enable 必须清 DisabledUntil");
            e.Enabled.Should().BeTrue("/enable 不动 Enabled 标志");
        }
    }

    [Fact]
    public async Task Create_WithQuotaFields_EchoedInResponse()
    {
        await LoginAsAdminAsync();
        DateTimeOffset expires = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "quota-echo",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat",
            quotaCallLimit = 1000,
            quotaTokenLimit = 5_000_000L,
            quotaExpiresAt = expires,
        });
        created.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement data = created.GetProperty("data");
        data.GetProperty("quotaCallLimit").GetInt32().Should().Be(1000);
        data.GetProperty("quotaTokenLimit").GetInt64().Should().Be(5_000_000L);
        data.GetProperty("quotaExpiresAt").GetDateTimeOffset().Should().Be(expires);
        data.GetProperty("quotaUsedCalls").GetInt64().Should().Be(0, "新建行用量从零累计");
        data.GetProperty("quotaUsedTokens").GetInt64().Should().Be(0);
        data.GetProperty("quotaExceededAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Update_RelaxingQuota_ClearsExceededAt_TighteningSetsIt()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "quota-reeval",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat",
            quotaCallLimit = 50,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        // 模拟 QuotaTracker 已累计用量并置位配额禁用
        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ParseAiProvider e = await ctx.ParseAiProviders.FirstAsync(p => p.Id == id);
            e.QuotaUsedCalls = 100;
            e.QuotaExceededAt = DateTimeOffset.UtcNow.AddHours(-1);
            await ctx.SaveChangesAsync();
        }

        // 放宽（清除次数限额）→ 不再超限，自动清 QuotaExceededAt
        JsonElement relaxed = await PostAsync("/api/settings/ai-providers/update", new
        {
            id,
            name = "quota-reeval",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
            quotaCallLimit = (int?)null,
        });
        relaxed.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        relaxed.GetProperty("data").GetProperty("quotaExceededAt").ValueKind
            .Should().Be(JsonValueKind.Null, "限额清除后不再超限，配额禁用自动解除");
        relaxed.GetProperty("data").GetProperty("quotaUsedCalls").GetInt64()
            .Should().Be(100, "放宽限额不清累计用量");

        // 收紧（限额 <= 已用量）→ 立即置位
        JsonElement tightened = await PostAsync("/api/settings/ai-providers/update", new
        {
            id,
            name = "quota-reeval",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
            quotaCallLimit = 80,
        });
        tightened.GetProperty("data").GetProperty("quotaExceededAt").ValueKind
            .Should().NotBe(JsonValueKind.Null, "收紧至 80 <= 已用 100，立即进入配额禁用");
    }

    [Fact]
    public async Task ResetQuota_ClearsCountersAndExceededAt()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "quota-reset",
            type = "OpenAiCompatible",
            baseUrl = "https://api.deepseek.com",
            apiKey = (string?)null,
            model = "deepseek-chat",
            quotaCallLimit = 10,
            quotaTokenLimit = 1000L,
        });
        long id = created.GetProperty("data").GetProperty("id").GetInt64();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ParseAiProvider e = await ctx.ParseAiProviders.FirstAsync(p => p.Id == id);
            e.QuotaUsedCalls = 10;
            e.QuotaUsedTokens = 2000;
            e.QuotaExceededAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        JsonElement reset = await PostAsync("/api/settings/ai-providers/reset-quota", new { id });
        reset.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);

        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            ParseAiProvider e = await ctx.ParseAiProviders.AsNoTracking().FirstAsync(p => p.Id == id);
            e.QuotaUsedCalls.Should().Be(0, "reset-quota 清零已用次数");
            e.QuotaUsedTokens.Should().Be(0, "reset-quota 清零已用 token");
            e.QuotaExceededAt.Should().BeNull("reset-quota 解除配额禁用");
            e.QuotaCallLimit.Should().Be(10, "限额配置保持不变");
            e.QuotaTokenLimit.Should().Be(1000, "限额配置保持不变");
        }
    }

    [Fact]
    public async Task ResetQuota_UnknownId_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement resp = await PostAsync("/api/settings/ai-providers/reset-quota", new { id = 987654 });
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("不存在");
    }

    [Fact]
    public async Task Stats_Aggregates_Audit_AiCall_RecentWindow()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "stat-test",
            type = "Ollama",
            baseUrl = "http://localhost:11434",
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        long providerId = created.GetProperty("data").GetProperty("id").GetInt64();

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            // 6 条窗口内：4 成功（延迟 400/800/1500/2800）+ 2 失败（Timeout / Http5xx）
            ctx.AuditAiCalls.AddRange(
                new() { ProviderId = providerId, Success = true, LatencyMs = 400, Timestamp = now.AddMinutes(-5) },
                new() { ProviderId = providerId, Success = true, LatencyMs = 800, Timestamp = now.AddMinutes(-15) },
                new() { ProviderId = providerId, Success = true, LatencyMs = 1500, Timestamp = now.AddMinutes(-30) },
                new() { ProviderId = providerId, Success = true, LatencyMs = 2800, Timestamp = now.AddHours(-2) },
                new() { ProviderId = providerId, Success = false, LatencyMs = 5000, ErrorType = "Timeout", Timestamp = now.AddHours(-3) },
                new() { ProviderId = providerId, Success = false, LatencyMs = 1200, ErrorType = "Http5xx", Timestamp = now.AddHours(-4) },
                // 窗口外（30h 前）：不计
                new() { ProviderId = providerId, Success = true, LatencyMs = 999, Timestamp = now.AddHours(-30) }
            );
            await ctx.SaveChangesAsync();
        }

        JsonElement resp = await GetAsync($"/api/settings/ai-providers/{providerId}/stats?windowHours=24");
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement data = resp.GetProperty("data");
        data.GetProperty("providerId").GetInt64().Should().Be(providerId);
        data.GetProperty("windowHours").GetInt32().Should().Be(24);
        data.GetProperty("totalCalls").GetInt32().Should().Be(6, "30h 前那条不计入 24h 窗口");
        data.GetProperty("successCount").GetInt32().Should().Be(4);
        data.GetProperty("failedCount").GetInt32().Should().Be(2);
        // 仅成功调用计入延迟统计：平均 = (400+800+1500+2800)/4 = 1375
        data.GetProperty("avgLatencyMs").GetDouble().Should().BeApproximately(1375, 1);
        // 4 个排序值 400/800/1500/2800，p95 在 2800 附近（线性插值在 0.95*3=2.85 → 1500+0.85*1300=2605）
        data.GetProperty("p95LatencyMs").GetInt32().Should().BeGreaterThan(2000);

        // 24 个小时桶
        data.GetProperty("hourlyBuckets").GetArrayLength().Should().Be(24);
        // 错误分布：Timeout + Http5xx 各 1 条
        JsonElement[] errs = data.GetProperty("errorBreakdown").EnumerateArray().ToArray();
        errs.Should().HaveCount(2);
        errs.Should().Contain(e => e.GetProperty("errorType").GetString() == "Timeout");
        errs.Should().Contain(e => e.GetProperty("errorType").GetString() == "Http5xx");
        // 延迟直方图：固定 6 个桶
        data.GetProperty("latencyHistogram").GetArrayLength().Should().Be(6);
        // 最近调用：6 条（窗口内全部）
        data.GetProperty("recentCalls").GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task Stats_InvalidWindowHours_Returns1000()
    {
        await LoginAsAdminAsync();
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "stat-invalid",
            type = "Ollama",
            baseUrl = "http://localhost:11434",
            apiKey = (string?)null,
            model = "llama3",
        });
        long providerId = created.GetProperty("data").GetProperty("id").GetInt64();
        JsonElement resp = await GetAsync($"/api/settings/ai-providers/{providerId}/stats?windowHours=999");
        resp.GetProperty("code").GetInt32().Should().Be(ApiCode.BusinessError);
        resp.GetProperty("message").GetString().Should().Contain("windowHours");
    }

    [Fact]
    public async Task List_IncludesRecent7dCallStats()
    {
        await LoginAsAdminAsync();

        // 有调用的 provider：CRUD 单条返回时统计取默认值（未聚合）
        JsonElement created = await PostAsync("/api/settings/ai-providers", new
        {
            name = "list-stats",
            type = "Ollama",
            baseUrl = "http://localhost:11434",
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = false,
            priority = 100,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });
        long withId = created.GetProperty("data").GetProperty("id").GetInt64();
        created.GetProperty("data").GetProperty("calls7d").GetInt32().Should().Be(0, "Create 返回不聚合统计");
        created.GetProperty("data").GetProperty("successRate").ValueKind.Should().Be(JsonValueKind.Null);

        // 无调用的 provider：列表里三项指标应为 0 / null
        await PostAsync("/api/settings/ai-providers", new
        {
            name = "list-nostats",
            type = "Ollama",
            baseUrl = "http://localhost:11434",
            apiKey = (string?)null,
            model = "llama3",
            isPrimary = false,
            priority = 200,
            enabled = true,
            timeoutSeconds = 30,
            extraOptions = (string?)null,
        });

        IDbContextFactory<PmmDbContext> dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (PmmDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            // 7 天窗口内：3 成功（延迟 100/200/300）+ 1 失败；8 天前那条窗口外不计
            ctx.AuditAiCalls.AddRange(
                new() { ProviderId = withId, Success = true, LatencyMs = 100, Timestamp = now.AddHours(-1) },
                new() { ProviderId = withId, Success = true, LatencyMs = 200, Timestamp = now.AddHours(-5) },
                new() { ProviderId = withId, Success = true, LatencyMs = 300, Timestamp = now.AddDays(-2) },
                new() { ProviderId = withId, Success = false, LatencyMs = 9999, ErrorType = "Timeout", Timestamp = now.AddDays(-3) },
                new() { ProviderId = withId, Success = true, LatencyMs = 777, Timestamp = now.AddDays(-8) }
            );
            await ctx.SaveChangesAsync();
        }

        JsonElement list = await GetAsync("/api/settings/ai-providers");
        JsonElement[] data = list.GetProperty("data").EnumerateArray().ToArray();

        JsonElement withRow = data.First(e => e.GetProperty("name").GetString() == "list-stats");
        withRow.GetProperty("calls7d").GetInt32().Should().Be(4, "8 天前那条不计入 7 天窗口");
        withRow.GetProperty("successRate").GetInt32().Should().Be(75, "3 成功 / 4 总 = 75%");
        withRow.GetProperty("avgLatency").GetInt32().Should().Be(200, "仅成功调用：(100+200+300)/3 = 200");

        JsonElement noRow = data.First(e => e.GetProperty("name").GetString() == "list-nostats");
        noRow.GetProperty("calls7d").GetInt32().Should().Be(0);
        noRow.GetProperty("successRate").ValueKind.Should().Be(JsonValueKind.Null, "无调用 → 成功率 null（前端显示「—」）");
        noRow.GetProperty("avgLatency").ValueKind.Should().Be(JsonValueKind.Null);
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
