using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.AiCallChains;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Parse;

/// <summary>D.3.3 AiCallOrchestrator — 多级升级 + 质量门 + 429 升级 + 瞬时重试 + Audit 写入</summary>
/// <remarks>
/// 覆盖：
///   1. 主一击成功（providersAttempted=1，audit=1 success）
///   2. 主瞬时错误 1 次后重试成功（providersAttempted=1，瞬时重试不计级数）
///   3. 主逻辑错误 → 备成功（providersAttempted=2）
///   4. 主备全失败（providersAttempted=2，Outcome.Success=false）
///   5. Resolver 返回空 → providersAttempted=0
///   6. 主瞬时错误 2 次（重试耗尽）→ 升级到备成功
///   7. 三级全失败 → 三级都被调用（多级升级，不再卡死 2 级）
///   8. 超多 provider 全失败 → 升级级数封顶 MaxHardCap（成本护栏）
///   9. 配置漂移（DB 有 Type 但 DI 无实现）→ 记 ConfigError + 跳到下一级
///  10. 健康追踪：接口故障失败后调一次 EvaluateAsync，成功不调
///  11. 低置信度（结果不满意）→ 无条件升级到下一级
///  12. 低置信度不触发健康追踪自动禁用（provider 健康，只是结果不够好）
///  13. 限流 429 → 立即升级，不在本级瞬时重试
///  14. per-provider 阈值生效：高阈值 provider 的合格结果在低阈值下会被接受 / 高阈值下被拒
///  15. 升级轨迹 Attempts 完整记录（级号 + 升级原因）
///  16. 全部低置信 → 升级链耗尽失败
/// </remarks>
public sealed class AiCallOrchestratorTests
{
    private static AiParseResult OkResult(double conf = 0.9) => new("Inception", 2010, "movie", Season: null, Episode: null, EpisodeEnd: null, conf);

    [Fact]
    public async Task PrimarySuccess_OneAttempt_AuditSuccess()
    {
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult()] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary],
            resolverOrder: [Resolution(1, AiProviderType.Ollama, isPrimary: true)],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: 42);

        outcome.Success.Should().BeTrue();
        outcome.Result.Should().NotBeNull();
        outcome.WinningProviderId.Should().Be(1);
        outcome.ProvidersAttempted.Should().Be(1);
        outcome.Attempts.Should().ContainSingle().Which.Success.Should().BeTrue();
        primary.CallCount.Should().Be(1);
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == 42 && e.Success && e.ErrorType == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryTransientThenSuccess_OneAttempt_RetryNotCountedAgainstCap()
    {
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            ScriptedExceptions = { [0] = new AiProviderTransientException("DNS fail") },
            NextResults = [OkResult()],   // 第二次（重试）才返回成功
        };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary],
            resolverOrder: [Resolution(1, AiProviderType.Ollama, isPrimary: true)],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue();
        outcome.ProvidersAttempted.Should().Be(1, "瞬时重试不消耗级数额度");
        primary.CallCount.Should().Be(2);
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == null && e.Success && e.ErrorType == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryLogicalFailure_BackupSuccess_TwoAttempts()
    {
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            ScriptedExceptions = { [0] = new AiProviderLogicalException("invalid model", 400) },
        };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };

        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: 7);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2);
        outcome.ProvidersAttempted.Should().Be(2);
        primary.CallCount.Should().Be(1);
        backup.CallCount.Should().Be(1);

        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == 7 && !e.Success && e.ErrorType == "Http4xx"), Arg.Any<CancellationToken>());
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 2 && e.MediaItemId == 7 && e.Success && e.ErrorType == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BothFail_OutcomeFailureWithSummary_TwoAttempts()
    {
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            ScriptedExceptions = { [0] = new AiProviderLogicalException("schema", null) },
        };
        FakeProvider backup = new(AiProviderType.Anthropic)
        {
            ScriptedExceptions = { [0] = new AiProviderLogicalException("invalid key", 401) },
        };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: 9);

        outcome.Success.Should().BeFalse();
        outcome.WinningProviderId.Should().BeNull();
        outcome.ProvidersAttempted.Should().Be(2);
        outcome.FailureSummary.Should().Contain("耗尽").And.Contain("Ollama").And.Contain("Anthropic");

        await audit.Received(2).WriteAsync(
            Arg.Is<AuditAiCallEntry>(e => e.MediaItemId == 9 && !e.Success), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoProvidersAvailable_ZeroAttempts_NoAuditWrites()
    {
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [new FakeProvider(AiProviderType.Ollama)],
            resolverOrder: [],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeFalse();
        outcome.ProvidersAttempted.Should().Be(0);
        outcome.FailureSummary.Should().Contain("未配置可用 AI 提供商");
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditAiCallEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryTransientRetryExhausted_FallsToBackup_TwoAttempts()
    {
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            // 第 0 次（首调）+ 第 1 次（瞬时重试）均瞬时失败 → 瞬时重试已耗尽 → 升级
            ScriptedExceptions =
            {
                [0] = new AiProviderTransientException("TCP1"),
                [1] = new AiProviderTransientException("TCP2"),
            },
        };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };

        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2);
        outcome.ProvidersAttempted.Should().Be(2);
        primary.CallCount.Should().Be(2);   // 1 首调 + 1 瞬时重试

        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == null && !e.Success && e.ErrorType == "Transient"), Arg.Any<CancellationToken>());
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 2 && e.MediaItemId == null && e.Success && e.ErrorType == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThreeProviders_AllTriedInOrder_WhenAllFail_MultiLevelEscalation()
    {
        // 多级升级：3 个 provider 全失败 → 三级都被调用（不再卡死 2 级）
        FakeProvider p1 = new(AiProviderType.Ollama) { ScriptedExceptions = { [0] = new AiProviderLogicalException("x") } };
        FakeProvider p2 = new(AiProviderType.Anthropic) { ScriptedExceptions = { [0] = new AiProviderLogicalException("y") } };
        FakeProvider p3 = new(AiProviderType.OpenAiCompatible) { NextResults = [OkResult()] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [p1, p2, p3],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
                Resolution(3, AiProviderType.OpenAiCompatible, isPrimary: false),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue("第 3 级最终成功");
        outcome.WinningProviderId.Should().Be(3);
        outcome.ProvidersAttempted.Should().Be(3);
        p1.CallCount.Should().Be(1);
        p2.CallCount.Should().Be(1);
        p3.CallCount.Should().Be(1, "升级到第 3 级");
        outcome.Attempts.Should().HaveCount(3);
        outcome.Attempts![2].Level.Should().Be(3);
    }

    [Fact]
    public async Task ManyProviders_EscalationCappedAt_MaxHardCap()
    {
        // 成本护栏：即使 Resolver 返回超过 MaxHardCap 个全失败 provider，也只升级到 MaxHardCap 级
        FakeProvider only = new(AiProviderType.Ollama) { AlwaysThrow = new AiProviderLogicalException("boom") };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        // 同 Type 多条 resolution 全部路由到同一个 FakeProvider（按 Type 路由）；用不同 ProviderId 模拟多级
        List<AiProviderResolution> order = [];
        for (int i = 1; i <= AiCallChain.MaxHardCap + 3; i++)
            order.Add(Resolution(i, AiProviderType.Ollama, isPrimary: i == 1));

        AiCallOrchestrator sut = NewSut(providers: [only], resolverOrder: order, audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeFalse();
        outcome.ProvidersAttempted.Should().Be(AiCallChain.MaxHardCap, "升级级数封顶 MaxHardCap");
        only.CallCount.Should().Be(AiCallChain.MaxHardCap);
    }

    [Fact]
    public async Task ResolverConfigDrift_NoMatchingProvider_AuditConfigError_SkipsToNext()
    {
        // 数据库里有 Qwen 行，但 DI 没注册 QwenProvider → 写 ConfigError + 走下一级
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [backup],   // 注意没有 Qwen
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2);
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == null && !e.Success && e.LatencyMs == 0 && e.ErrorType == "ConfigError"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HealthTrackerCalledOncePerFailedProvider_NeverOnSuccess()
    {
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            ScriptedExceptions = { [0] = new AiProviderLogicalException("nope", 401) },
        };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };

        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        IAiProviderHealthTracker tracker = Substitute.For<IAiProviderHealthTracker>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit,
            healthTracker: tracker);

        await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        await tracker.Received(1).EvaluateAsync(1, Arg.Any<CancellationToken>());
        await tracker.DidNotReceive().EvaluateAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LowConfidence_Escalates_ToBackup_AndAuditsLowConfidence()
    {
        // 主返回低置信度（0.5 < 阈值 0.7）→ 结果不满意 → 无条件升级到备；备返回 0.9 合格 → 命中
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(conf: 0.5)] };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult(conf: 0.9)] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true, threshold: 0.7),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false, threshold: 0.7),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: 11);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2);
        outcome.ProvidersAttempted.Should().Be(2);
        // 主被记一条「低置信度升级」审计行
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == 11 && !e.Success && e.ErrorType == "LowConfidence" && e.Confidence == 0.5), Arg.Any<CancellationToken>());
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 2 && e.MediaItemId == 11 && e.Success && e.ErrorType == null), Arg.Any<CancellationToken>());
        outcome.Attempts![0].ErrorType.Should().Be("LowConfidence");
        outcome.Attempts![0].Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task LowConfidence_DoesNotTriggerHealthTracker()
    {
        // 低置信度是「结果不够好」而非「接口故障」→ 不应触发自动禁用评估
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(conf: 0.5)] };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult(conf: 0.9)] };
        IAiProviderHealthTracker tracker = Substitute.For<IAiProviderHealthTracker>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true, threshold: 0.7),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false, threshold: 0.7),
            ],
            audit: Substitute.For<IAuditAiCallWriter>(),
            healthTracker: tracker);

        await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        await tracker.DidNotReceive().EvaluateAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RateLimit_Escalates_Immediately_NoTransientRetry()
    {
        // 429 限流：本级达上限，立即升级，不在本 provider 瞬时重试（CallCount=1）
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            ScriptedExceptions = { [0] = new AiProviderRateLimitException("HTTP 429 quota") },
        };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2);
        primary.CallCount.Should().Be(1, "限流不在本级瞬时重试，直接升级");
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && e.MediaItemId == null && !e.Success && e.ErrorType == "RateLimit"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalRpmThrottled_SkipsProvider_NoHttpCall_EscalatesToNext()
    {
        // 本地 RPM 限流：主 provider 本窗口已达 RpmLimit → 跳过（不实际调用、不计配额、不 Record）、记 RateLimit 审计、升级到备
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult()] };  // 有结果但不应被调用到
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        IAiProviderQuotaTracker quota = Substitute.For<IAiProviderQuotaTracker>();
        IAiProviderRpmGate gate = Substitute.For<IAiProviderRpmGate>();
        gate.IsThrottled(1, 5).Returns(true);   // 仅 provider 1（RpmLimit=5）限流；provider 2（null）默认放行

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true, rpmLimit: 5),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit,
            quotaTracker: quota,
            rpmGate: gate);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: 7);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2, "主被 RPM 限流跳过，升级到备");
        primary.CallCount.Should().Be(0, "被 RPM 限流跳过，不实际发起调用");
        backup.CallCount.Should().Be(1);
        // 主记一条 RateLimit 跳过轨迹（LatencyMs=0，没发 HTTP）
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e => e.ProviderId == 1 && !e.Success && e.ErrorType == "RateLimit" && e.LatencyMs == 0), Arg.Any<CancellationToken>());
        // 跳过不计配额（没发 HTTP）；备正常计一次
        await quota.DidNotReceive().RecordUsageAsync(1, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await quota.Received(1).RecordUsageAsync(2, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        // 只有实际发起的备被 Record；被跳过的主不 Record
        gate.Received(1).Record(2);
        gate.DidNotReceive().Record(1);
    }

    [Fact]
    public async Task PerProviderThreshold_HighThresholdRejects_LowThresholdAccepts()
    {
        // 主阈值 0.85，返回 0.8 → 不达标升级；备阈值 0.7，同样 0.8 → 达标命中
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(conf: 0.8)] };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult(conf: 0.8)] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true, threshold: 0.85),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false, threshold: 0.7),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(2, "0.8 < 0.85 升级；0.8 ≥ 0.7 命中");
        outcome.ProvidersAttempted.Should().Be(2);
    }

    [Fact]
    public async Task ThresholdBoundary_EqualToThreshold_IsAccepted()
    {
        // 边界：置信度恰等于阈值视为达标（>=）
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(conf: 0.7)] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary],
            resolverOrder: [Resolution(1, AiProviderType.Ollama, isPrimary: true, threshold: 0.7)],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue("0.7 >= 0.7 视为达标");
        outcome.ProvidersAttempted.Should().Be(1);
    }

    [Fact]
    public async Task AllLowConfidence_ExhaustsChain_Fails()
    {
        // 全部级别都不满意 → 升级链耗尽失败
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(conf: 0.4)] };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult(conf: 0.5)] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true, threshold: 0.7),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false, threshold: 0.7),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeFalse();
        outcome.ProvidersAttempted.Should().Be(2);
        outcome.Attempts.Should().HaveCount(2);
        outcome.Attempts!.Should().OnlyContain(a => a.ErrorType == "LowConfidence");
    }

    [Fact]
    public async Task Attempts_Trajectory_RecordsLevelsAndReasons()
    {
        // 轨迹完整性：第 1 级限流升级、第 2 级低置信升级、第 3 级成功
        FakeProvider p1 = new(AiProviderType.Ollama) { ScriptedExceptions = { [0] = new AiProviderRateLimitException("429") } };
        FakeProvider p2 = new(AiProviderType.Anthropic) { NextResults = [OkResult(conf: 0.5)] };
        FakeProvider p3 = new(AiProviderType.OpenAiCompatible) { NextResults = [OkResult(conf: 0.95)] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();

        AiCallOrchestrator sut = NewSut(
            providers: [p1, p2, p3],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true, threshold: 0.7),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false, threshold: 0.7),
                Resolution(3, AiProviderType.OpenAiCompatible, isPrimary: false, threshold: 0.7),
            ],
            audit: audit);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: null);

        outcome.Success.Should().BeTrue();
        outcome.WinningProviderId.Should().Be(3);
        outcome.Attempts.Should().HaveCount(3);
        outcome.Attempts![0].ErrorType.Should().Be("RateLimit");
        outcome.Attempts![1].ErrorType.Should().Be("LowConfidence");
        outcome.Attempts![2].Success.Should().BeTrue();
        outcome.Attempts!.Select(a => a.Level).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task SuccessEntry_CapturesMonitoringFields_TokensModelConfidenceChainLevel()
    {
        // 监控增强：成功行应落 token / 模型 / 置信度 / HTTP 200 / 升级链 Id / 级序 / 主标 / 原文
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(conf: 0.92)] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary],
            resolverOrder: [Resolution(1, AiProviderType.Ollama, isPrimary: true)],
            audit: audit);

        await sut.ExecuteAsync(SampleRequest(), mediaItemId: 5);

        await audit.Received(1).WriteAsync(
            Arg.Is<AuditAiCallEntry>(e =>
                e.ProviderId == 1 && e.Success && e.AttemptLevel == 1 && e.IsPrimary
                && e.Model == "m" && e.PromptTokens == 11 && e.CompletionTokens == 22
                && e.Confidence == 0.92 && e.HttpStatus == 200
                && e.ChainId != null && e.RequestText != null && e.ResponseText == "resp-json"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChainTimeout_WritesTimeoutAuditRow_AndThrowsChineseOce()
    {
        // 审计修复项 2：链路总超时触发时，正在挂死的当级 provider 此前没有任何 Audit 行——
        // 必须补写 ErrorType=Timeout 行（标明 provider / 已等待时长），且异常 message 中文化供下游 MarkFailed 可读
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        IReadOnlyList<AiProviderResolution> order = [Resolution(1, AiProviderType.Ollama, isPrimary: true)];
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(order);
        AiCallOrchestrator sut = new(resolver, new HangingAiParser(), audit,
            Substitute.For<IAiProviderHealthTracker>(), Substitute.For<IAiProviderQuotaTracker>(),
            Substitute.For<IAiProviderRpmGate>(), NullLogger<AiCallOrchestrator>.Instance)
        {
            ChainTimeoutOverride = TimeSpan.FromMilliseconds(150),   // 单测不真实等 30s 计时窗
        };

        Func<Task> act = () => sut.ExecuteAsync(SampleRequest(), mediaItemId: 42);

        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Message.Should().Contain("AI 调用链路超时").And.Contain("Ollama-1");
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(e =>
            e.ProviderId == 1 && e.MediaItemId == 42 && !e.Success
            && e.ErrorType == "Timeout" && e.AttemptLevel == 1 && e.IsPrimary
            && e.ErrorDetail != null && e.ErrorDetail.Contains("已等待")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExternalCancel_PropagatesOce_WithoutTimeoutAuditRow()
    {
        // 区分「真外部取消」与「链路超时」：外部 ct 已请求取消时按取消语义原样上抛，不补 Timeout 审计行
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        IReadOnlyList<AiProviderResolution> order = [Resolution(1, AiProviderType.Ollama, isPrimary: true)];
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(order);
        AiCallOrchestrator sut = new(resolver, new HangingAiParser(), audit,
            Substitute.For<IAiProviderHealthTracker>(), Substitute.For<IAiProviderQuotaTracker>(),
            Substitute.For<IAiProviderRpmGate>(), NullLogger<AiCallOrchestrator>.Instance)
        {
            ChainTimeoutOverride = TimeSpan.FromSeconds(30),   // 拉长链路超时，确保先发生外部取消
        };
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => sut.ExecuteAsync(SampleRequest(), mediaItemId: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditAiCallEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QuotaTracker_RecordsUsage_OnFailureAndSuccessPaths()
    {
        // 套餐配额计量：主失败与备成功各计一次（失败请求也可能计费）；token 与审计行同值——
        // 失败级异常未携带 token 诊断 → null（只计次数），成功级取协议返回的 usage（FakeAiParser 桩 11/22）
        FakeProvider primary = new(AiProviderType.Ollama)
        {
            ScriptedExceptions = { [0] = new AiProviderLogicalException("invalid model", 400) },
        };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        IAiProviderQuotaTracker quota = Substitute.For<IAiProviderQuotaTracker>();
        AiCallOrchestrator sut = NewSut(
            providers: [primary, backup],
            resolverOrder:
            [
                Resolution(1, AiProviderType.Ollama, isPrimary: true),
                Resolution(2, AiProviderType.Anthropic, isPrimary: false),
            ],
            audit: audit,
            quotaTracker: quota);

        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), mediaItemId: 7);

        outcome.Success.Should().BeTrue();
        await quota.Received(1).RecordUsageAsync(1, null, null, Arg.Any<CancellationToken>());
        await quota.Received(1).RecordUsageAsync(2, 11, 22, Arg.Any<CancellationToken>());
    }

    private static AiCallOrchestrator NewSut(
        IEnumerable<FakeProvider> providers,
        IReadOnlyList<AiProviderResolution> resolverOrder,
        IAuditAiCallWriter audit,
        IAiProviderHealthTracker? healthTracker = null,
        IAiProviderQuotaTracker? quotaTracker = null,
        IAiProviderRpmGate? rpmGate = null)
    {
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(resolverOrder);
        healthTracker ??= Substitute.For<IAiProviderHealthTracker>();
        quotaTracker ??= Substitute.For<IAiProviderQuotaTracker>();
        // 默认不限流（IsThrottled 返 bool 默认 false）；需要限流的用例显式注入
        rpmGate ??= Substitute.For<IAiProviderRpmGate>();
        return new AiCallOrchestrator(resolver, new FakeAiParser(providers), audit, healthTracker, quotaTracker, rpmGate, NullLogger<AiCallOrchestrator>.Instance);
    }

    private static AiParseRequest SampleRequest() => new("Inception.2010.mkv");

    private static AiProviderResolution Resolution(long id, AiProviderType type, bool isPrimary, double threshold = 0.7, int? rpmLimit = null) =>
        new(id, type, $"{type}-{id}", isPrimary,
            new AiProviderEndpoint("https://x", "sk", "m", 30), threshold, rpmLimit);

    /// <summary>测试用 provider 替身：按调用次数返回预设异常或结果；AlwaysThrow 设置时每次都抛同一异常</summary>
    /// <remarks>不再实现已删除的 IAiProvider，仅作为 FakeAiParser 按 Type 路由委派的脚本化桩（Type + ParseAsync 由 FakeAiParser 直接调用）。</remarks>
    private sealed class FakeProvider
    {
        public FakeProvider(AiProviderType type) { Type = type; }
        public AiProviderType Type { get; }
        public int CallCount { get; private set; }
        public Dictionary<int, Exception> ScriptedExceptions { get; } = new();
        public List<AiParseResult> NextResults { get; init; } = new();
        public Exception? AlwaysThrow { get; init; }

        public Task<AiParseResult> ParseAsync(AiProviderEndpoint endpoint, AiParseRequest request, CancellationToken ct = default)
        {
            int idx = CallCount;
            CallCount++;
            if (AlwaysThrow is not null)
                return Task.FromException<AiParseResult>(AlwaysThrow);
            if (ScriptedExceptions.TryGetValue(idx, out Exception? ex))
                return Task.FromException<AiParseResult>(ex);
            if (NextResults.Count == 0)
                return Task.FromException<AiParseResult>(new InvalidOperationException($"FakeProvider({Type}) 无预设结果（call={idx}）"));
            AiParseResult r = NextResults[0];
            NextResults.RemoveAt(0);
            return Task.FromResult(r);
        }
    }

    /// <summary>测试用挂死解析门面：一直等待直到取消（模拟 provider 无响应挂死，供链路超时用例）</summary>
    private sealed class HangingAiParser : IAiParser
    {
        public bool Supports(AiProviderType protocol) => true;

        public async Task<AiParseOutcome> ParseAsync(AiProviderType protocol, AiProviderEndpoint endpoint, AiParseRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("不可达：上一行必然因取消抛出");
        }
    }

    /// <summary>测试用 IAiParser：按协议路由到对应 FakeProvider（模拟生产 AiProtocolParser + 协议策略 + 反解）</summary>
    private sealed class FakeAiParser : IAiParser
    {
        private readonly IReadOnlyDictionary<AiProviderType, FakeProvider> _byType;
        public FakeAiParser(IEnumerable<FakeProvider> providers) { _byType = providers.ToDictionary(p => p.Type); }
        public bool Supports(AiProviderType protocol) => _byType.ContainsKey(protocol);
        public async Task<AiParseOutcome> ParseAsync(AiProviderType protocol, AiProviderEndpoint endpoint, AiParseRequest request, CancellationToken ct = default)
        {
            if (!_byType.TryGetValue(protocol, out FakeProvider? p))
                throw new AiProviderLogicalException($"无实现：{protocol}");
            AiParseResult r = await p.ParseAsync(endpoint, request, ct);
            // 桩出 token + 原文，供监控字段断言（成功路径）
            return new AiParseOutcome(r, RequestText: $"req:{request.FileName}", ResponseText: "resp-json", PromptTokens: 11, CompletionTokens: 22);
        }
    }
}
