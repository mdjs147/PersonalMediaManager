using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>AiProviderHealthTracker（D3.4）— 滚动 10min ≥5 失败 → DisabledUntil = now+30min</summary>
/// <remarks>
/// 覆盖 D.3.5 「自动禁用触发条件 1 条」+ 边界场景：
/// - 5 条窗口内失败 → 禁用 30min（命中阈值）
/// - 4 条窗口内失败 → 不禁用（差 1 条不到阈值）
/// - 5 条但有 1 条在窗口外 → 不禁用（窗口边界排除）
/// - 成功不算 → 5 条成功 + 1 条失败 → 不禁用
/// - 已在禁用窗口内（DisabledUntil&gt;now）→ 不刷新（避免「连续失败把禁用无限延后」）
/// </remarks>
public sealed class AiProviderHealthTrackerTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public AiProviderHealthTrackerTests(PmmDbContextTestFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task FiveFailuresWithinWindow_TriggersDisable30Min()
    {
        DateTimeOffset now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync();
        await SeedAuditAsync(pid, count: 5, success: false, baseTime: now.AddMinutes(-3));

        AiProviderHealthTracker tracker = NewTracker(now);
        await tracker.EvaluateAsync(pid);

        DateTimeOffset? disabledUntil = await ReadDisabledUntilAsync(pid);
        disabledUntil.Should().NotBeNull();
        disabledUntil!.Value.Should().Be(now.AddMinutes(AiProviderHealthTracker.CooldownMinutes));
    }

    [Fact]
    public async Task FourFailuresWithinWindow_DoesNotDisable()
    {
        DateTimeOffset now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync();
        await SeedAuditAsync(pid, count: 4, success: false, baseTime: now.AddMinutes(-2));

        await NewTracker(now).EvaluateAsync(pid);

        (await ReadDisabledUntilAsync(pid)).Should().BeNull("4 < 5 阈值不触发");
    }

    [Fact]
    public async Task FailuresOutsideWindow_NotCounted()
    {
        DateTimeOffset now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync();
        // 4 条窗口内 + 1 条 11 分钟前（窗口外）= 4 有效 < 5
        await SeedAuditAsync(pid, count: 4, success: false, baseTime: now.AddMinutes(-2));
        await SeedSingleAuditAsync(pid, success: false, at: now.AddMinutes(-11));

        await NewTracker(now).EvaluateAsync(pid);
        (await ReadDisabledUntilAsync(pid)).Should().BeNull("窗口外行不参与统计");
    }

    [Fact]
    public async Task SuccessNotCounted()
    {
        DateTimeOffset now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync();
        await SeedAuditAsync(pid, count: 5, success: true, baseTime: now.AddMinutes(-3));
        await SeedSingleAuditAsync(pid, success: false, at: now.AddMinutes(-1));

        await NewTracker(now).EvaluateAsync(pid);
        (await ReadDisabledUntilAsync(pid)).Should().BeNull("成功调用不计入失败窗口");
    }

    [Fact]
    public async Task AlreadyDisabled_DoesNotRefresh()
    {
        DateTimeOffset now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset existing = now.AddMinutes(20);   // 仍在冷却
        long pid = await SeedProviderAsync(disabledUntil: existing);
        await SeedAuditAsync(pid, count: 10, success: false, baseTime: now.AddMinutes(-2));

        await NewTracker(now).EvaluateAsync(pid);

        DateTimeOffset? after = await ReadDisabledUntilAsync(pid);
        after.Should().Be(existing, "已在冷却窗口内不刷新（手动 /enable 才能解禁）");
    }

    [Fact]
    public async Task PreviouslyDisabledExpired_TreatedAsAvailable_CanRedisable()
    {
        DateTimeOffset now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset expired = now.AddMinutes(-5);    // 已过期
        long pid = await SeedProviderAsync(disabledUntil: expired);
        await SeedAuditAsync(pid, count: 5, success: false, baseTime: now.AddMinutes(-3));

        await NewTracker(now).EvaluateAsync(pid);

        DateTimeOffset? after = await ReadDisabledUntilAsync(pid);
        after!.Value.Should().Be(now.AddMinutes(AiProviderHealthTracker.CooldownMinutes),
            "冷却已过 + 又攒满 5 次失败 → 再次禁用 30min");
    }

    [Fact]
    public async Task UnknownProviderId_NoOp()
    {
        AiProviderHealthTracker tracker = NewTracker(DateTimeOffset.UtcNow);
        Func<Task> act = () => tracker.EvaluateAsync(999999);
        await act.Should().NotThrowAsync();
    }

    private AiProviderHealthTracker NewTracker(DateTimeOffset now) =>
        new(new FixtureDbContextFactory(_fixture), new FixedClock(now));

    private async Task<long> SeedProviderAsync(DateTimeOffset? disabledUntil = null)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        ParseAiProvider provider = new()
        {
            Name = $"p-{Guid.NewGuid():N}",
            Type = AiProviderType.OpenAiCompatible,
            BaseUrl = "https://x.example.com",
            Model = "m",
            Enabled = true,
            DisabledUntil = disabledUntil,
            TimeoutSeconds = 30,
        };
        ctx.ParseAiProviders.Add(provider);
        await ctx.SaveChangesAsync();
        return provider.Id;
    }

    private async Task SeedAuditAsync(long providerId, int count, bool success, DateTimeOffset baseTime)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        for (int i = 0; i < count; i++)
        {
            ctx.AuditAiCalls.Add(new AuditAiCall
            {
                ProviderId = providerId,
                Success = success,
                LatencyMs = 100,
                ErrorType = success ? null : "Http5xx",
                Timestamp = baseTime.AddSeconds(i * 5),
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task SeedSingleAuditAsync(long providerId, bool success, DateTimeOffset at)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        ctx.AuditAiCalls.Add(new AuditAiCall
        {
            ProviderId = providerId,
            Success = success,
            LatencyMs = 100,
            ErrorType = success ? null : "Http5xx",
            Timestamp = at,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<DateTimeOffset?> ReadDisabledUntilAsync(long providerId)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        return await ctx.ParseAiProviders.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => p.DisabledUntil)
            .FirstOrDefaultAsync();
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) { UtcNow = utcNow; }
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FixtureDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly PmmDbContextTestFixture _fixture;
        public FixtureDbContextFactory(PmmDbContextTestFixture fixture) { _fixture = fixture; }
        public PmmDbContext CreateDbContext() => _fixture.CreateContext();
    }
}
