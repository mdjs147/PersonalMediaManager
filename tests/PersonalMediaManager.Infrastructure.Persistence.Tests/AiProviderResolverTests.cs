using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;
using PersonalMediaManager.Infrastructure.Platform.Security;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>AiProviderResolver（D3.2）— IsPrimary + Priority 排序 + DisabledUntil 过滤 + ApiKey 解密</summary>
public sealed class AiProviderResolverTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;
    private readonly IProtectedFieldService _crypto;

    public AiProviderResolverTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
        _crypto = new DataProtectionFieldService(new EphemeralDataProtectionProvider());
    }

    [Fact]
    public async Task PrimaryFirst_ThenByPriorityAsc_DisabledFiltered_ApiKeyDecrypted()
    {
        await ClearProvidersAsync();

        // 准备：1 个主 + 2 个备（不同 Priority）+ 1 个 Enabled=false + 1 个 DisabledUntil 未来
        await SeedProvidersAsync(
            new SeedDef("ollama-local",  AiProviderType.Ollama,           IsPrimary: false, Priority: 200, Enabled: true,  ApiKey: null,   DisabledUntil: null),
            new SeedDef("qwen-main",     AiProviderType.OpenAiCompatible,             IsPrimary: true,  Priority: 100, Enabled: true,  ApiKey: "sk-q", DisabledUntil: null),
            new SeedDef("ds-backup-50",  AiProviderType.Anthropic,         IsPrimary: false, Priority: 50,  Enabled: true,  ApiKey: "sk-d", DisabledUntil: null),
            new SeedDef("disabled",      AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 10,  Enabled: false, ApiKey: "sk-x", DisabledUntil: null),
            new SeedDef("cooling-down",  AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 20,  Enabled: true,  ApiKey: "sk-c", DisabledUntil: DateTimeOffset.UtcNow.AddMinutes(15)));

        FixedClock clock = new(DateTimeOffset.UtcNow);
        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, clock);

        IReadOnlyList<AiProviderResolution> result = await resolver.ResolveOrderedAsync();

        result.Select(r => r.Name).Should().Equal("qwen-main", "ds-backup-50", "ollama-local");
        result[0].IsPrimary.Should().BeTrue();
        result[0].Endpoint.ApiKey.Should().Be("sk-q", "Resolver 应已解密 ApiKey");
        result[2].Endpoint.ApiKey.Should().BeNull("Ollama 本地部署 ApiKey 可空");
    }

    [Fact]
    public async Task NoPrimary_OnlyBackups_OrderedByPriority()
    {
        await ClearProvidersAsync();
        await SeedProvidersAsync(
            new SeedDef("b1", AiProviderType.OpenAiCompatible,     IsPrimary: false, Priority: 300, Enabled: true, ApiKey: "k1", DisabledUntil: null),
            new SeedDef("b2", AiProviderType.Anthropic, IsPrimary: false, Priority: 100, Enabled: true, ApiKey: "k2", DisabledUntil: null),
            new SeedDef("b3", AiProviderType.Ollama,   IsPrimary: false, Priority: 200, Enabled: true, ApiKey: null, DisabledUntil: null));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(DateTimeOffset.UtcNow));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();
        r.Select(x => x.Name).Should().Equal("b2", "b3", "b1");
    }

    [Fact]
    public async Task PrimaryDisabled_DemotedToOnlyBackupsRemain()
    {
        await ClearProvidersAsync();
        await SeedProvidersAsync(
            new SeedDef("primary-dead", AiProviderType.OpenAiCompatible, IsPrimary: true,  Priority: 100, Enabled: true,  ApiKey: "k", DisabledUntil: DateTimeOffset.UtcNow.AddMinutes(5)),
            new SeedDef("backup-live",  AiProviderType.Anthropic, IsPrimary: false, Priority: 200, Enabled: true,  ApiKey: "k2", DisabledUntil: null));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(DateTimeOffset.UtcNow));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();
        r.Should().HaveCount(1);
        r[0].Name.Should().Be("backup-live");
    }

    [Fact]
    public async Task AllDisabledOrCooling_ReturnsEmpty()
    {
        await ClearProvidersAsync();
        await SeedProvidersAsync(
            new SeedDef("d1", AiProviderType.OpenAiCompatible,     IsPrimary: true,  Priority: 100, Enabled: false, ApiKey: "k1", DisabledUntil: null),
            new SeedDef("d2", AiProviderType.Anthropic, IsPrimary: false, Priority: 200, Enabled: true,  ApiKey: "k2", DisabledUntil: DateTimeOffset.UtcNow.AddMinutes(10)));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(DateTimeOffset.UtcNow));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();
        r.Should().BeEmpty();
    }

    [Fact]
    public async Task DisabledUntilInPast_TreatedAsAvailable()
    {
        await ClearProvidersAsync();
        // 已经过了冷却时间但 DisabledUntil 字段还在（没被清空）— 应视为可用
        await SeedProvidersAsync(
            new SeedDef("recovered", AiProviderType.OpenAiCompatible, IsPrimary: true, Priority: 100, Enabled: true, ApiKey: "k", DisabledUntil: DateTimeOffset.UtcNow.AddMinutes(-5)));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(DateTimeOffset.UtcNow));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();
        r.Should().HaveCount(1);
        r[0].Name.Should().Be("recovered");
    }

    [Fact]
    public async Task QuotaExceededOrPlanExpired_Filtered_FutureExpiryKept()
    {
        await ClearProvidersAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 配额超限（QuotaExceededAt 非空，即使很久以前置位也不自动恢复）与套餐到期（QuotaExpiresAt<=now）都剔除；
        // 未到期（QuotaExpiresAt 在未来）照常可用
        await SeedProvidersAsync(
            new SeedDef("quota-exceeded", AiProviderType.OpenAiCompatible, IsPrimary: true,  Priority: 10, Enabled: true, ApiKey: "k1", DisabledUntil: null,
                QuotaExceededAt: now.AddDays(-3)),
            new SeedDef("plan-expired",   AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 20, Enabled: true, ApiKey: "k2", DisabledUntil: null,
                QuotaExpiresAt: now.AddMinutes(-1)),
            new SeedDef("plan-active",    AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 30, Enabled: true, ApiKey: "k3", DisabledUntil: null,
                QuotaExpiresAt: now.AddDays(30)));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(now));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();

        r.Should().HaveCount(1, "配额禁用与套餐到期都从升级链剔除（与 Enabled / DisabledUntil 三态并列）");
        r[0].Name.Should().Be("plan-active");
    }

    [Fact]
    public async Task PeriodQuota_ExceededWithinWindow_Filtered_ButCrossWindowAutoRecovers()
    {
        await ClearProvidersAsync();
        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        // A：周期内超限（ResetAt 在未来 + UsedCalls≥Limit）→ 窗口内剔除
        // B：周期计数虽超限但 ResetAt 已过（now≥ResetAt，已跨窗口）→ 视同归零放行（无需人工解除，自动恢复）
        // C：启用周期但未超限 → 正常可用
        await SeedProvidersAsync(
            new SeedDef("period-over-inwindow", AiProviderType.OpenAiCompatible, IsPrimary: true, Priority: 10, Enabled: true, ApiKey: "k1", DisabledUntil: null,
                QuotaPeriod: AiQuotaPeriod.Daily, QuotaPeriodResetAt: now.AddHours(6), QuotaPeriodCallLimit: 5, QuotaPeriodUsedCalls: 5),
            new SeedDef("period-over-crosswindow", AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 20, Enabled: true, ApiKey: "k2", DisabledUntil: null,
                QuotaPeriod: AiQuotaPeriod.Daily, QuotaPeriodResetAt: now.AddHours(-1), QuotaPeriodCallLimit: 5, QuotaPeriodUsedCalls: 5),
            new SeedDef("period-under", AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 30, Enabled: true, ApiKey: "k3", DisabledUntil: null,
                QuotaPeriod: AiQuotaPeriod.Daily, QuotaPeriodResetAt: now.AddHours(6), QuotaPeriodCallLimit: 5, QuotaPeriodUsedCalls: 2));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(now));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();

        // 窗口内超限剔除；跨窗口(now≥ResetAt)自动恢复放行；未超限照常——剩余按 Priority 升序
        r.Select(x => x.Name).Should().Equal("period-over-crosswindow", "period-under");
    }

    [Fact]
    public async Task PeriodQuota_TokenLimitExceededWithinWindow_Filtered()
    {
        await ClearProvidersAsync();
        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        // 周期 token 达限（窗口内）同样剔除；仅次数维度未设限
        await SeedProvidersAsync(
            new SeedDef("period-token-over", AiProviderType.OpenAiCompatible, IsPrimary: true, Priority: 10, Enabled: true, ApiKey: "k1", DisabledUntil: null,
                QuotaPeriod: AiQuotaPeriod.Monthly, QuotaPeriodResetAt: now.AddDays(10), QuotaPeriodTokenLimit: 1000, QuotaPeriodUsedTokens: 1000),
            new SeedDef("period-ok", AiProviderType.OpenAiCompatible, IsPrimary: false, Priority: 20, Enabled: true, ApiKey: "k2", DisabledUntil: null,
                QuotaPeriod: AiQuotaPeriod.Monthly, QuotaPeriodResetAt: now.AddDays(10), QuotaPeriodTokenLimit: 1000, QuotaPeriodUsedTokens: 300));

        AiProviderResolver resolver = new(new FixtureDbContextFactory(_fixture), _crypto, new FixedClock(now));
        IReadOnlyList<AiProviderResolution> r = await resolver.ResolveOrderedAsync();

        // 周期 token 达限的窗口内剔除，未达限的保留
        r.Select(x => x.Name).Should().Equal("period-ok");
    }

    private async Task ClearProvidersAsync()
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        ctx.ParseAiProviders.RemoveRange(ctx.ParseAiProviders);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedProvidersAsync(params SeedDef[] defs)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        foreach (SeedDef d in defs)
        {
            ParseAiProvider entity = new()
            {
                Name = d.Name,
                Type = d.Type,
                BaseUrl = d.Type == AiProviderType.Ollama ? "http://localhost:11434" : "https://x.example.com",
                ApiKeyEncrypted = d.ApiKey is null ? null : _crypto.Protect(d.ApiKey),
                Model = "m",
                IsPrimary = d.IsPrimary,
                Priority = d.Priority,
                Enabled = d.Enabled,
                DisabledUntil = d.DisabledUntil,
                TimeoutSeconds = 30,
                QuotaExceededAt = d.QuotaExceededAt,
                QuotaExpiresAt = d.QuotaExpiresAt,
                QuotaPeriod = d.QuotaPeriod,
                QuotaPeriodResetAt = d.QuotaPeriodResetAt,
                QuotaPeriodCallLimit = d.QuotaPeriodCallLimit,
                QuotaPeriodTokenLimit = d.QuotaPeriodTokenLimit,
                QuotaPeriodUsedCalls = d.QuotaPeriodUsedCalls,
                QuotaPeriodUsedTokens = d.QuotaPeriodUsedTokens,
            };
            ctx.ParseAiProviders.Add(entity);
        }
        await ctx.SaveChangesAsync();
    }

    private sealed record SeedDef(
        string Name, AiProviderType Type, bool IsPrimary, int Priority, bool Enabled, string? ApiKey, DateTimeOffset? DisabledUntil,
        DateTimeOffset? QuotaExceededAt = null, DateTimeOffset? QuotaExpiresAt = null,
        AiQuotaPeriod QuotaPeriod = AiQuotaPeriod.None, DateTimeOffset? QuotaPeriodResetAt = null,
        int? QuotaPeriodCallLimit = null, long? QuotaPeriodTokenLimit = null,
        long QuotaPeriodUsedCalls = 0, long QuotaPeriodUsedTokens = 0);

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) { UtcNow = utcNow; }
        public DateTimeOffset UtcNow { get; }
    }

    /// <summary>把 PmmDbContextTestFixture 包成 IDbContextFactory，让被测代码可直接 await CreateDbContextAsync</summary>
    private sealed class FixtureDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly PmmDbContextTestFixture _fixture;
        public FixtureDbContextFactory(PmmDbContextTestFixture fixture) { _fixture = fixture; }
        public PmmDbContext CreateDbContext() => _fixture.CreateContext();
    }
}
