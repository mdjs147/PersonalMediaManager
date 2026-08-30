using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>QuotaPeriodMath — 周期额度下一自然边界计算（日/周/月 + 时区 + 恒严格大于 now + 非法时区兜底）</summary>
public sealed class QuotaPeriodMathTests
{
    [Fact]
    public void Daily_Utc_ReturnsNextMidnight()
    {
        DateTimeOffset now = new(2026, 7, 4, 12, 30, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(now, AiQuotaPeriod.Daily, "UTC")
            .Should().Be(new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Daily_Utc_AtExactMidnight_StillReturnsNextDay()
    {
        DateTimeOffset now = new(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(now, AiQuotaPeriod.Daily, "UTC")
            .Should().Be(new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero), "边界恒严格大于 now，00:00 整也进次日");
    }

    [Fact]
    public void Weekly_Utc_MidWeek_ReturnsNextMonday()
    {
        // 2026-07-04 为周六 → 下一个周一 2026-07-06
        DateTimeOffset sat = new(2026, 7, 4, 8, 0, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(sat, AiQuotaPeriod.Weekly, "UTC")
            .Should().Be(new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_Utc_OnMonday_ReturnsFollowingMonday()
    {
        // 2026-07-06 本身是周一 → 取 7 天后的下周一 2026-07-13（不取当天）
        DateTimeOffset mon = new(2026, 7, 6, 10, 0, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(mon, AiQuotaPeriod.Weekly, "UTC")
            .Should().Be(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Monthly_Utc_ReturnsFirstOfNextMonth()
    {
        DateTimeOffset mid = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(mid, AiQuotaPeriod.Monthly, "UTC")
            .Should().Be(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Monthly_Utc_December_RollsToNextYear()
    {
        DateTimeOffset dec = new(2026, 12, 20, 9, 0, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(dec, AiQuotaPeriod.Monthly, "UTC")
            .Should().Be(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(AiQuotaPeriod.Daily)]
    [InlineData(AiQuotaPeriod.Weekly)]
    [InlineData(AiQuotaPeriod.Monthly)]
    public void AllPeriods_ResultIsStrictlyAfterNow(AiQuotaPeriod period)
    {
        DateTimeOffset now = new(2026, 7, 4, 23, 59, 0, TimeSpan.Zero);
        QuotaPeriodMath.NextBoundary(now, period, "UTC").Should().BeAfter(now);
    }

    [Fact]
    public void NullTimeZone_UsesLocal_StillStrictlyAfterNow()
    {
        // tzId=null → 本机时区；不便断言绝对值（依赖 CI 机时区），但结果必严格大于 now
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuotaPeriodMath.NextBoundary(now, AiQuotaPeriod.Daily, null).Should().BeAfter(now);
    }

    [Fact]
    public void InvalidTimeZone_FallsBackToLocal_DoesNotThrow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Func<DateTimeOffset> act = () => QuotaPeriodMath.NextBoundary(now, AiQuotaPeriod.Daily, "Not/A_Real_Zone");
        act.Should().NotThrow("非法时区 id 回退本机时区兜底不抛");
    }
}
