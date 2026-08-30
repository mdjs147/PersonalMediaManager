using PersonalMediaManager.Domain.Aggregates.AiCallChains;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>D1.5 AiCallChain 聚合单元测试 — 多级升级硬上限（可配）+ 瞬时重试 1 次豁免不变式</summary>
public sealed class AiCallChainTests
{
    [Fact]
    public void FreshChain_CanCallNext_AndCannotRetryYet()
    {
        AiCallChain chain = new();
        chain.CanCallNextProvider.Should().BeTrue();
        chain.ProvidersCalled.Should().Be(0);
    }

    [Fact]
    public void DefaultConstructor_UsesDefaultHardCap()
    {
        AiCallChain chain = new();
        chain.HardCap.Should().Be(AiCallChain.DefaultHardCap);
    }

    [Fact]
    public void Constructor_ClampsHardCapTo_OneToMax()
    {
        new AiCallChain(0).HardCap.Should().Be(1, "下限钳到 1");
        new AiCallChain(-5).HardCap.Should().Be(1);
        new AiCallChain(AiCallChain.MaxHardCap + 99).HardCap.Should().Be(AiCallChain.MaxHardCap, "上限钳到 MaxHardCap 成本护栏");
        new AiCallChain(5).HardCap.Should().Be(5, "区间内原样");
    }

    [Fact]
    public void BeginProviderCall_IncrementsCount_AndResetsTransientRetries()
    {
        AiCallChain chain = new(3);
        chain.BeginProviderCall();
        chain.ProvidersCalled.Should().Be(1);
        chain.TransientRetriesOnCurrent.Should().Be(0);
        chain.CanCallNextProvider.Should().BeTrue("还有升级额度");
    }

    [Fact]
    public void Calls_ReachingHardCap_CannotCallMore()
    {
        // 显式两级链：两级用尽即到顶
        AiCallChain chain = new(2);
        chain.BeginProviderCall();
        chain.BeginProviderCall();
        chain.ProvidersCalled.Should().Be(2);
        chain.CanCallNextProvider.Should().BeFalse("已达硬上限 2 级");
    }

    [Fact]
    public void BeginCall_AfterHardCap_Throws()
    {
        AiCallChain chain = new(2);
        chain.BeginProviderCall();
        chain.BeginProviderCall();
        Action act = () => chain.BeginProviderCall();
        act.Should().Throw<DomainException>().WithMessage("*硬上限*");
    }

    [Fact]
    public void MultiLevel_ThreeLevels_AllowsThreeCalls()
    {
        // 多级升级：3 级链允许 3 次调用（不再卡死 2 级）
        AiCallChain chain = new(3);
        chain.BeginProviderCall();
        chain.RecordProviderFailure();
        chain.CanCallNextProvider.Should().BeTrue("第 1 级失败后仍可升级");
        chain.BeginProviderCall();
        chain.RecordProviderFailure();
        chain.CanCallNextProvider.Should().BeTrue("第 2 级失败后仍可升级到第 3 级");
        chain.BeginProviderCall();
        chain.ProvidersCalled.Should().Be(3);
        chain.CanCallNextProvider.Should().BeFalse("3 级耗尽");
    }

    [Fact]
    public void RecordTransientError_WithinLimit_AllowsRetry()
    {
        AiCallChain chain = new(3);
        chain.BeginProviderCall();
        chain.CanRetryTransient.Should().BeTrue();
        chain.RecordTransientError();
        chain.TransientRetriesOnCurrent.Should().Be(1);
        chain.CanRetryTransient.Should().BeFalse("每 provider 仅 1 次瞬时重试");
    }

    [Fact]
    public void RecordTransientError_ResetOnNextProvider()
    {
        AiCallChain chain = new(3);
        chain.BeginProviderCall();
        chain.RecordTransientError();
        chain.BeginProviderCall();
        chain.TransientRetriesOnCurrent.Should().Be(0, "升级到下一级后瞬时重试计数清零");
    }

    [Fact]
    public void RecordSuccess_CompletesChain_BlocksFurtherCalls()
    {
        AiCallChain chain = new(3);
        chain.BeginProviderCall();
        chain.RecordSuccess();
        chain.CompletedSuccessfully.Should().BeTrue();
        chain.CanCallNextProvider.Should().BeFalse("成功后不再升级");
        chain.CanRetryTransient.Should().BeFalse();
    }

    [Fact]
    public void AllLevelsFail_ChainEnds_NoMoreSlots()
    {
        AiCallChain chain = new(2);
        chain.BeginProviderCall();
        chain.RecordProviderFailure();
        chain.BeginProviderCall();
        chain.RecordProviderFailure();
        chain.CanCallNextProvider.Should().BeFalse("各级耗尽");
        chain.ProvidersCalled.Should().Be(2);
    }
}
