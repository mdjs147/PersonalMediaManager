using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

/// <summary>AiChatClient — 主备遍历 + Audit_AiCall 全路径落审计（审计修复项 1）</summary>
/// <remarks>
/// 覆盖：
/// 1. 成功调用落 success 审计行（model / token / 耗时 / HTTP 200 / 级序 / 主标 / chainId / 原文，MediaItemId=null）
/// 2. 主失败 → 备成功：两行审计共享 chainId，级序 1→2，失败行错误分类 Http4xx + 状态码
/// 3. 全失败：抛 AiChatUnavailableException 且每级失败行均已落（RateLimit / Transient 分类正确）
/// 4. 空响应：按 Logical「返回空内容」落审计并升级到下一级
/// 5. 链路总超时：补写 ErrorType=Timeout 行（中文 detail 含等待时长）+ 抛中文「链路超时」异常
/// 6. 外部取消：原样上抛 OperationCanceledException，不落 Timeout 行（区分真取消与链路超时）
/// 7. 协议无实现（配置漂移）：落 ConfigError 行并走下一级
/// 8. 健康熔断评估（D3.4）与解析链同口径：仅「接口故障」（Transient/RateLimit/Http*/Logical）失败触发
///    EvaluateAsync；成功 / ConfigError / Timeout / 外部取消不触发——各场景在对应用例内随审计断言一并校验
/// 本测试项目无 mock 框架，沿用手写桩约定（FakeResolver / FakeProtocol / RecordingAuditWriter / RecordingHealthTracker）。
/// </remarks>
public sealed class AiChatClientTests
{
    private static AiChatRequest Req() => new("system 提示", "user 提示");

    private static AiProviderResolution Res(long id, AiProviderType type, bool isPrimary = false) =>
        new(id, type, $"{type}-{id}", isPrimary, new AiProviderEndpoint("http://x", null, $"model-{id}", 30));

    private static AiChatClient NewSut(
        IReadOnlyList<AiProviderResolution> order,
        RecordingAuditWriter audit,
        TimeSpan? chainTimeout = null,
        RecordingHealthTracker? health = null,
        RecordingQuotaTracker? quota = null,
        params IAiProtocol[] protocols) =>
        new(new FakeResolver(order), protocols, audit, health ?? new RecordingHealthTracker(),
            quota ?? new RecordingQuotaTracker(), NullLogger<AiChatClient>.Instance)
        {
            ChainTimeoutOverride = chainTimeout,
        };

    [Fact]
    public async Task Success_WritesSuccessAuditRow_WithTokensModelChain()
    {
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol ollama = new(AiProviderType.Ollama, (_, _) => Task.FromResult(new AiCompletion("{\"ok\":1}", 11, 22)));
        AiChatClient sut = NewSut([Res(1, AiProviderType.Ollama, isPrimary: true)], audit, health: health, protocols: [ollama]);

        AiChatResult r = await sut.CompleteAsync(Req());

        r.Content.Should().Be("{\"ok\":1}");
        r.ProviderId.Should().Be(1);
        AuditAiCallEntry e = audit.Entries.Should().ContainSingle().Subject;
        e.Success.Should().BeTrue();
        e.ProviderId.Should().Be(1);
        e.MediaItemId.Should().BeNull("对话类调用与具体媒体文件无关，以 null 区分于解析链");
        e.Model.Should().Be("model-1");
        e.PromptTokens.Should().Be(11);
        e.CompletionTokens.Should().Be(22);
        e.HttpStatus.Should().Be(200);
        e.AttemptLevel.Should().Be(1);
        e.IsPrimary.Should().BeTrue();
        e.ChainId.Should().NotBeNullOrEmpty();
        e.Confidence.Should().BeNull("对话类调用无置信度概念");
        e.RequestText.Should().Be("user 提示", "与解析链口径一致：仅存 user 提示词");
        e.ResponseText.Should().Contain("ok");
        e.ErrorType.Should().BeNull();
        e.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
        health.Evaluated.Should().BeEmpty("成功调用不触发健康熔断评估");
    }

    [Fact]
    public async Task PrimaryFails_BackupSucceeds_BothAudited_SharedChainId()
    {
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol bad = new(AiProviderType.Ollama, (_, _) => throw new AiProviderLogicalException("无效请求", 401));
        FakeProtocol good = new(AiProviderType.Anthropic, (_, _) => Task.FromResult(new AiCompletion("ok", 1, 2)));
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true), Res(2, AiProviderType.Anthropic)],
            audit, health: health, protocols: [bad, good]);

        AiChatResult r = await sut.CompleteAsync(Req());

        r.ProviderId.Should().Be(2);
        audit.Entries.Should().HaveCount(2);
        audit.Entries[0].Success.Should().BeFalse();
        audit.Entries[0].ErrorType.Should().Be("Http4xx");
        audit.Entries[0].HttpStatus.Should().Be(401);
        audit.Entries[0].ErrorDetail.Should().Contain("无效请求");
        audit.Entries[0].AttemptLevel.Should().Be(1);
        audit.Entries[1].Success.Should().BeTrue();
        audit.Entries[1].AttemptLevel.Should().Be(2);
        audit.Entries[1].ChainId.Should().Be(audit.Entries[0].ChainId, "同一次 CompleteAsync 的多级尝试共享升级链 Id");
        health.Evaluated.Should().Equal(new long[] { 1 }, "Http4xx 属接口故障 → 失败级触发熔断评估；成功级不触发");
    }

    [Fact]
    public async Task AllFail_ThrowsUnavailable_EveryFailureRowLanded()
    {
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol p1 = new(AiProviderType.Ollama, (_, _) => throw new AiProviderRateLimitException("HTTP 429 配额耗尽"));
        FakeProtocol p2 = new(AiProviderType.Anthropic, (_, _) => throw new AiProviderTransientException("DNS 解析失败"));
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true), Res(2, AiProviderType.Anthropic)],
            audit, health: health, protocols: [p1, p2]);

        Func<Task> act = () => sut.CompleteAsync(Req());

        await act.Should().ThrowAsync<AiChatUnavailableException>().WithMessage("*全部 AI 提供商均失败*");
        audit.Entries.Should().HaveCount(2);
        audit.Entries[0].ErrorType.Should().Be("RateLimit");
        audit.Entries[1].ErrorType.Should().Be("Transient");
        audit.Entries.Should().OnlyContain(e => !e.Success && e.MediaItemId == null);
        health.Evaluated.Should().Equal(new long[] { 1, 2 }, "RateLimit / Transient 均属接口故障，每级失败写完审计后各触发一次熔断评估");
    }

    [Fact]
    public async Task EmptyContent_AuditsLogical_EscalatesToNext()
    {
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol empty = new(AiProviderType.Ollama, (_, _) => Task.FromResult(new AiCompletion("   ")));
        FakeProtocol good = new(AiProviderType.Anthropic, (_, _) => Task.FromResult(new AiCompletion("ok")));
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true), Res(2, AiProviderType.Anthropic)],
            audit, health: health, protocols: [empty, good]);

        AiChatResult r = await sut.CompleteAsync(Req());

        r.ProviderId.Should().Be(2);
        audit.Entries.Should().HaveCount(2);
        audit.Entries[0].Success.Should().BeFalse();
        audit.Entries[0].ErrorType.Should().Be("Logical");
        audit.Entries[0].ErrorDetail.Should().Be("返回空内容");
        audit.Entries[0].HttpStatus.Should().Be(200, "HTTP 成功但内容不可用");
        health.Evaluated.Should().Equal(new long[] { 1 }, "空内容按 Logical 归类，属接口故障 → 触发熔断评估");
    }

    [Fact]
    public async Task ChainTimeout_WritesTimeoutRow_ThrowsChineseUnavailable()
    {
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol hang = new(AiProviderType.Ollama, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AiCompletion("不可达");
        });
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true)],
            audit, chainTimeout: TimeSpan.FromMilliseconds(150), health: health, protocols: [hang]);

        Func<Task> act = () => sut.CompleteAsync(Req());

        (await act.Should().ThrowAsync<AiChatUnavailableException>())
            .Which.Message.Should().Contain("链路超时").And.Contain("Ollama-1");
        AuditAiCallEntry e = audit.Entries.Should().ContainSingle("挂死的当级 provider 必须留下审计痕迹").Subject;
        e.Success.Should().BeFalse();
        e.ErrorType.Should().Be("Timeout");
        e.ErrorDetail.Should().Contain("已等待").And.Contain("Ollama-1");
        e.AttemptLevel.Should().Be(1);
        health.Evaluated.Should().BeEmpty("Timeout 是全链预算耗尽，归因当级仅作诊断线索，不触发熔断评估（与解析链口径一致）");
    }

    [Fact]
    public async Task ExternalCancel_PropagatesOce_NoTimeoutAuditRow()
    {
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol hang = new(AiProviderType.Ollama, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AiCompletion("不可达");
        });
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true)],
            audit, chainTimeout: TimeSpan.FromSeconds(30), health: health, protocols: [hang]);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => sut.CompleteAsync(Req(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("外部取消按取消语义原样上抛");
        audit.Entries.Should().BeEmpty("外部取消不是链路超时，不写 Timeout 行");
        health.Evaluated.Should().BeEmpty("外部取消非接口故障，不触发熔断评估");
    }

    [Fact]
    public async Task NoProtocolImplementation_AuditsConfigError_SkipsToNext()
    {
        // 配置漂移：第 1 级解析为 Ollama 但 DI 未注册对应协议 → ConfigError 落审计后走下一级
        RecordingAuditWriter audit = new();
        RecordingHealthTracker health = new();
        FakeProtocol good = new(AiProviderType.Anthropic, (_, _) => Task.FromResult(new AiCompletion("ok")));
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true), Res(2, AiProviderType.Anthropic)],
            audit, health: health, protocols: [good]);

        AiChatResult r = await sut.CompleteAsync(Req());

        r.ProviderId.Should().Be(2);
        audit.Entries.Should().HaveCount(2);
        audit.Entries[0].ErrorType.Should().Be("ConfigError");
        audit.Entries[0].ErrorDetail.Should().Contain("无 IAiProtocol 实现");
        health.Evaluated.Should().BeEmpty("ConfigError 是配置漂移非 provider 故障，不触发熔断评估（与解析链口径一致）");
    }

    [Fact]
    public async Task QuotaTracker_RecordsUsage_OnFailureAndSuccessPaths()
    {
        // 套餐配额计量与解析链同口径：主失败与备成功各计一次（失败请求也可能计费）；
        // token 与审计行同值——失败级异常无 usage 诊断 → null，成功级取协议返回的 usage
        RecordingAuditWriter audit = new();
        RecordingQuotaTracker quota = new();
        FakeProtocol bad = new(AiProviderType.Ollama,
            (_, _) => Task.FromException<AiCompletion>(new AiProviderLogicalException("bad request", 400)));
        FakeProtocol good = new(AiProviderType.Anthropic,
            (_, _) => Task.FromResult(new AiCompletion("ok", 11, 22)));
        AiChatClient sut = NewSut(
            [Res(1, AiProviderType.Ollama, isPrimary: true), Res(2, AiProviderType.Anthropic)],
            audit, quota: quota, protocols: [bad, good]);

        AiChatResult r = await sut.CompleteAsync(Req());

        r.ProviderId.Should().Be(2);
        quota.Recorded.Should().HaveCount(2, "成功失败都计量，且与审计行一一对应");
        quota.Recorded[0].Should().Be((1L, (int?)null, (int?)null));
        quota.Recorded[1].Should().Be((2L, (int?)11, (int?)22));
    }

    /// <summary>测试用 resolver 桩：固定返回预设升级序列</summary>
    private sealed class FakeResolver : IAiProviderResolver
    {
        private readonly IReadOnlyList<AiProviderResolution> _order;
        public FakeResolver(IReadOnlyList<AiProviderResolution> order) => _order = order;
        public Task<IReadOnlyList<AiProviderResolution>> ResolveOrderedAsync(CancellationToken ct = default) => Task.FromResult(_order);
    }

    /// <summary>测试用协议桩：按委托脚本返回补全 / 抛异常 / 挂死</summary>
    private sealed class FakeProtocol : IAiProtocol
    {
        private readonly Func<AiProtocolRequest, CancellationToken, Task<AiCompletion>> _handler;
        public FakeProtocol(AiProviderType type, Func<AiProtocolRequest, CancellationToken, Task<AiCompletion>> handler)
        {
            Protocol = type;
            _handler = handler;
        }

        public AiProviderType Protocol { get; }
        public Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default) => _handler(request, ct);
    }

    /// <summary>测试用审计写入器：记录全部落库入参供断言</summary>
    private sealed class RecordingAuditWriter : IAuditAiCallWriter
    {
        public List<AuditAiCallEntry> Entries { get; } = new();

        public Task WriteAsync(AuditAiCallEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    /// <summary>测试用健康追踪桩：按序记录被评估的 providerId 供断言（D3.4 熔断触发口径）</summary>
    private sealed class RecordingHealthTracker : IAiProviderHealthTracker
    {
        public List<long> Evaluated { get; } = new();

        public Task EvaluateAsync(long providerId, CancellationToken ct = default)
        {
            Evaluated.Add(providerId);
            return Task.CompletedTask;
        }
    }

    /// <summary>测试用配额计量桩：按序记录 (providerId, promptTokens, completionTokens) 供断言（成功失败都应计量）</summary>
    private sealed class RecordingQuotaTracker : IAiProviderQuotaTracker
    {
        public List<(long ProviderId, int? PromptTokens, int? CompletionTokens)> Recorded { get; } = new();

        public Task RecordUsageAsync(long providerId, int? promptTokens, int? completionTokens, CancellationToken ct = default)
        {
            Recorded.Add((providerId, promptTokens, completionTokens));
            return Task.CompletedTask;
        }
    }
}
