using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>AiProviderQuotaTracker — 调用后计次/计 token + 超限幂等置 QuotaExceededAt + 只发一次告警</summary>
/// <remarks>
/// 覆盖套餐配额计量的核心行为：
/// - 未配置限额：计数照常累计但永不置位、不发告警
/// - 次数达限：置位 QuotaExceededAt（= FixedClock now）+ 恰好一次 ai.provider_quota_exceeded 告警
/// - token 达限：prompt+completion 合计达 QuotaTokenLimit 即置位
/// - token 为 null（厂商未返回 usage / 失败无诊断）：只计次数，token 累计不变
/// - 已置位后再次计量：计数继续累计（对账口径）但不重复置位、不重复发事件（条件化 ExecuteUpdate 幂等）
/// - provider 不存在：no-op 不抛
/// </remarks>
public sealed class AiProviderQuotaTrackerTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public AiProviderQuotaTrackerTests(PmmDbContextTestFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task NoLimitsConfigured_AccumulatesButNeverMarksExceeded()
    {
        long pid = await SeedProviderAsync();
        RecordingAlertService alerts = new();
        AiProviderQuotaTracker tracker = NewTracker(DateTimeOffset.UtcNow, alerts);

        await tracker.RecordUsageAsync(pid, 100, 200);
        await tracker.RecordUsageAsync(pid, 300, 400);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaUsedCalls.Should().Be(2);
        row.QuotaUsedTokens.Should().Be(1000, "token 累计 = (100+200)+(300+400)");
        row.QuotaExceededAt.Should().BeNull("未配置任何限额 = 不限，永不置位");
        alerts.Raised.Should().BeEmpty();
    }

    [Fact]
    public async Task CallLimitReached_MarksExceeded_AndAlertsExactlyOnce()
    {
        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync(callLimit: 2);
        RecordingAlertService alerts = new();
        AiProviderQuotaTracker tracker = NewTracker(now, alerts);

        await tracker.RecordUsageAsync(pid, 10, 5);
        (await ReadProviderAsync(pid)).QuotaExceededAt.Should().BeNull("1 < 2 未达限");
        alerts.Raised.Should().BeEmpty();

        await tracker.RecordUsageAsync(pid, 10, 5);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaUsedCalls.Should().Be(2);
        row.QuotaExceededAt.Should().Be(now, "达到 QuotaCallLimit 即置位（IClock now）");
        alerts.Raised.Should().ContainSingle();
        alerts.Raised[0].Event.Should().Be(WebhookEvents.AiProviderQuotaExceeded);
        alerts.Raised[0].AlertKey.Should().Contain(pid.ToString(), "alertKey 按 provider 粒度隔离抑制窗口");
    }

    [Fact]
    public async Task TokenLimitReached_MarksExceeded()
    {
        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync(tokenLimit: 100);
        RecordingAlertService alerts = new();
        AiProviderQuotaTracker tracker = NewTracker(now, alerts);

        await tracker.RecordUsageAsync(pid, 60, 50);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaUsedCalls.Should().Be(1, "次数不设限也照常累计");
        row.QuotaUsedTokens.Should().Be(110);
        row.QuotaExceededAt.Should().Be(now, "110 >= 100 token 达限置位");
        alerts.Raised.Should().ContainSingle();
    }

    [Fact]
    public async Task NullTokens_CountsCallsOnly()
    {
        long pid = await SeedProviderAsync(tokenLimit: 100);
        RecordingAlertService alerts = new();
        AiProviderQuotaTracker tracker = NewTracker(DateTimeOffset.UtcNow, alerts);

        await tracker.RecordUsageAsync(pid, promptTokens: null, completionTokens: null);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaUsedCalls.Should().Be(1);
        row.QuotaUsedTokens.Should().Be(0, "厂商未返回 usage 的调用只计次数");
        row.QuotaExceededAt.Should().BeNull();
        alerts.Raised.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterExceeded_SubsequentCalls_DoNotDuplicateAlertOrTimestamp()
    {
        DateTimeOffset first = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset later = first.AddMinutes(30);
        long pid = await SeedProviderAsync(callLimit: 1);
        RecordingAlertService alerts = new();

        await NewTracker(first, alerts).RecordUsageAsync(pid, 1, 1);
        // 已超限后继续有调用进来（在途请求）：计数继续累计对账，但置位与告警幂等不重复
        await NewTracker(later, alerts).RecordUsageAsync(pid, 1, 1);
        await NewTracker(later, alerts).RecordUsageAsync(pid, 1, 1);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaUsedCalls.Should().Be(3);
        row.QuotaExceededAt.Should().Be(first, "仅首次越线置位（条件化 ExecuteUpdate 保留首次时刻）");
        alerts.Raised.Should().ContainSingle("置位成功那一次才发事件，二次/并发调用不重复发");
    }

    [Fact]
    public async Task UnknownProviderId_NoOp()
    {
        RecordingAlertService alerts = new();
        AiProviderQuotaTracker tracker = NewTracker(DateTimeOffset.UtcNow, alerts);

        Func<Task> act = () => tracker.RecordUsageAsync(999999, 1, 1);

        await act.Should().NotThrowAsync();
        alerts.Raised.Should().BeEmpty();
    }

    // ============ 周期滚动额度（跨窗口惰性重置）============

    [Fact]
    public async Task PeriodQuota_FirstUse_SetsBoundary_ThenAccumulatesWithinWindow()
    {
        // UTC 时区便于确定边界：每日额度到次日 00:00 UTC 重置
        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset nextBoundary = new(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync(period: AiQuotaPeriod.Daily, periodTimeZone: "UTC");

        // 首次：ResetAt==null → 归零重置 + 落定次日边界
        await NewTracker(now, new RecordingAlertService()).RecordUsageAsync(pid, 10, 5);
        ParseAiProvider afterFirst = await ReadProviderAsync(pid);
        afterFirst.QuotaPeriodUsedCalls.Should().Be(1);
        afterFirst.QuotaPeriodUsedTokens.Should().Be(15);
        afterFirst.QuotaPeriodResetAt.Should().Be(nextBoundary, "首次记账落定 UTC 次日 00:00 边界");

        // 同窗口内再记（now < ResetAt）→ 累加，不重置边界
        await NewTracker(now, new RecordingAlertService()).RecordUsageAsync(pid, 20, 0);
        ParseAiProvider afterSecond = await ReadProviderAsync(pid);
        afterSecond.QuotaPeriodUsedCalls.Should().Be(2);
        afterSecond.QuotaPeriodUsedTokens.Should().Be(35);
        afterSecond.QuotaPeriodResetAt.Should().Be(nextBoundary, "窗口内边界不变");
    }

    [Fact]
    public async Task PeriodQuota_CrossingWindow_ResetsCount_AndAdvancesBoundary()
    {
        DateTimeOffset day1 = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync(period: AiQuotaPeriod.Daily, periodTimeZone: "UTC");

        await NewTracker(day1, new RecordingAlertService()).RecordUsageAsync(pid, 10, 5);
        await NewTracker(day1, new RecordingAlertService()).RecordUsageAsync(pid, 10, 5);
        (await ReadProviderAsync(pid)).QuotaPeriodUsedCalls.Should().Be(2);

        // 跨到次日（now ≥ 之前的 ResetAt 2026-07-05 00:00）→ 惰性归零重置
        DateTimeOffset day2 = new(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        await NewTracker(day2, new RecordingAlertService()).RecordUsageAsync(pid, 3, 2);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaPeriodUsedCalls.Should().Be(1, "跨窗口归零后本次 +1");
        row.QuotaPeriodUsedTokens.Should().Be(5, "跨窗口归零后仅本次 token");
        row.QuotaPeriodResetAt.Should().Be(new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero), "边界推进到再次日");
        row.QuotaUsedCalls.Should().Be(3, "终身累计不受周期重置影响");
    }

    [Fact]
    public async Task PeriodQuota_None_LeavesPeriodCountersUntouched()
    {
        long pid = await SeedProviderAsync(period: AiQuotaPeriod.None);

        await NewTracker(DateTimeOffset.UtcNow, new RecordingAlertService()).RecordUsageAsync(pid, 10, 5);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaUsedCalls.Should().Be(1, "终身照常累计");
        row.QuotaPeriodUsedCalls.Should().Be(0, "未启用周期额度，周期计数保持不动");
        row.QuotaPeriodResetAt.Should().BeNull("未启用周期额度不落定边界");
    }

    [Fact]
    public async Task PeriodQuota_OverLimit_DoesNotSetQuotaExceededAt()
    {
        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        long pid = await SeedProviderAsync(period: AiQuotaPeriod.Daily, periodTimeZone: "UTC", periodCallLimit: 1);

        await NewTracker(now, new RecordingAlertService()).RecordUsageAsync(pid, 1, 1);
        await NewTracker(now, new RecordingAlertService()).RecordUsageAsync(pid, 1, 1);

        ParseAiProvider row = await ReadProviderAsync(pid);
        row.QuotaPeriodUsedCalls.Should().Be(2, "已超周期上限但计数继续对账");
        row.QuotaExceededAt.Should().BeNull("周期超限不写 QuotaExceededAt——软禁用交给 Resolver 滚动窗口过滤，跨窗口自动恢复");
    }

    private AiProviderQuotaTracker NewTracker(DateTimeOffset now, IAlertService alert) =>
        new(new FixtureDbContextFactory(_fixture), new FixedClock(now), alert,
            NullLogger<AiProviderQuotaTracker>.Instance);

    private async Task<long> SeedProviderAsync(int? callLimit = null, long? tokenLimit = null,
        AiQuotaPeriod period = AiQuotaPeriod.None, string? periodTimeZone = null,
        int? periodCallLimit = null, long? periodTokenLimit = null)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        ParseAiProvider provider = new()
        {
            Name = $"q-{Guid.NewGuid():N}",
            Type = AiProviderType.OpenAiCompatible,
            BaseUrl = "https://x.example.com",
            Model = "m",
            Enabled = true,
            TimeoutSeconds = 30,
            QuotaCallLimit = callLimit,
            QuotaTokenLimit = tokenLimit,
            QuotaPeriod = period,
            QuotaPeriodTimeZone = periodTimeZone,
            QuotaPeriodCallLimit = periodCallLimit,
            QuotaPeriodTokenLimit = periodTokenLimit,
        };
        ctx.ParseAiProviders.Add(provider);
        await ctx.SaveChangesAsync();
        return provider.Id;
    }

    private async Task<ParseAiProvider> ReadProviderAsync(long providerId)
    {
        await using PmmDbContext ctx = _fixture.CreateContext();
        return await ctx.ParseAiProviders.AsNoTracking().FirstAsync(p => p.Id == providerId);
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

    /// <summary>测试用告警桩：记录全部 RaiseAsync 入参供「只发一次」断言</summary>
    private sealed class RecordingAlertService : IAlertService
    {
        public List<(string AlertKey, string Event, object Data)> Raised { get; } = new();

        public Task RaiseAsync(string alertKey, string @event, object data, CancellationToken ct = default)
        {
            Raised.Add((alertKey, @event, data));
            return Task.CompletedTask;
        }
    }
}
