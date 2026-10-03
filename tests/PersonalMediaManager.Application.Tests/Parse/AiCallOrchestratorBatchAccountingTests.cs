using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;
namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class AiCallOrchestratorBatchAccountingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPhysicalUsageIsChargedOnceEvenWhenOwnerIsCancelled(bool cancelOwner)
    {
        using CancellationTokenSource owner = new();
        Parser parser = new() { AfterSend = cancelOwner ? () => owner.Cancel() : null };
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new AiProviderResolution[]
            { new(7, AiProviderType.OpenAiCompatible, "fixture", true, new("https://fixture.invalid", null, "fixture")) });
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        IAiProviderQuotaTracker quota = Substitute.For<IAiProviderQuotaTracker>();
        IAiProviderRpmGate rpm = Substitute.For<IAiProviderRpmGate>();
        AiCallOrchestrator Service() => new(resolver, parser, audit, Substitute.For<IAiProviderHealthTracker>(), quota, rpm,
            NullLogger<AiCallOrchestrator>.Instance);
        bool cancelled = false;
        await AiBatchPipeline.RunAsync([
            async _ => { try { await Service().ExecuteAsync(new("Alpha.mkv"), 1, owner.Token); }
                catch (OperationCanceledException) { cancelled = true; } },
            async ct => { (await Service().ExecuteAsync(new("Beta.mkv"), 2, ct)).Success.Should().BeTrue(); }
        ], new());
        parser.Calls.Should().Be(1);
        cancelled.Should().Be(cancelOwner);
        rpm.Received(1).Record(7);
        await quota.Received(1).RecordUsageAsync(7, 20, 5, Arg.Any<CancellationToken>());
        await quota.Received(1).RecordUsageAsync(Arg.Any<long>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationDuringAuditStillChargesAlreadySentPhysicalRequest()
    {
        using CancellationTokenSource owner = new();
        Parser parser = new();
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new AiProviderResolution[]
            { new(7, AiProviderType.OpenAiCompatible, "fixture", true, new("https://fixture.invalid", null, "fixture")) });
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        audit.WriteAsync(Arg.Is<AuditAiCallEntry>(entry => entry.MediaItemId == 1), Arg.Any<CancellationToken>())
            .Returns(info => { owner.Cancel(); return Task.FromCanceled(info.Arg<CancellationToken>()); });
        IAiProviderQuotaTracker quota = Substitute.For<IAiProviderQuotaTracker>();
        AiCallOrchestrator Service() => new(resolver, parser, audit, Substitute.For<IAiProviderHealthTracker>(),
            quota, Substitute.For<IAiProviderRpmGate>(), NullLogger<AiCallOrchestrator>.Instance);
        await AiBatchPipeline.RunAsync([
            async _ => { try { await Service().ExecuteAsync(new("Alpha.mkv"), 1, owner.Token); }
                catch (OperationCanceledException) { } },
            async ct => { (await Service().ExecuteAsync(new("Beta.mkv"), 2, ct)).Success.Should().BeTrue(); }
        ], new());
        await quota.Received(1).RecordUsageAsync(7, 20, 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PhysicalChunksCannotSpendTheSameRemainingQuotaTwice()
    {
        LimitedQuota quota = new();
        SplitParser parser = new();
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new AiProviderResolution[]
            { new(7, AiProviderType.OpenAiCompatible, "fixture", true, new("https://fixture.invalid", null, "fixture")) });
        AiCallOrchestrator service = new(resolver, parser, Substitute.For<IAuditAiCallWriter>(),
            Substitute.For<IAiProviderHealthTracker>(), quota, Substitute.For<IAiProviderRpmGate>(), NullLogger<AiCallOrchestrator>.Instance);
        await service.ExecuteAsync(new("Alpha.mkv"), 1);
        parser.Sent.Should().Be(1);
        quota.Calls.Should().Be(1);
        quota.Tokens.Should().Be(7);
    }
    [Fact]
    public async Task ReadyResultKeepsRemainingChainBudgetDuringSerialContinuation()
    {
        Parser parser = new();
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new AiProviderResolution[]
            { new(7, AiProviderType.OpenAiCompatible, "fixture", true, new("https://fixture.invalid", null, "fixture")) });
        AiCallOrchestrator Service() => new(resolver, parser, Substitute.For<IAuditAiCallWriter>(),
            Substitute.For<IAiProviderHealthTracker>(), Substitute.For<IAiProviderQuotaTracker>(),
            Substitute.For<IAiProviderRpmGate>(), NullLogger<AiCallOrchestrator>.Instance)
            { ChainTimeoutOverride = TimeSpan.FromMilliseconds(100) };
        await AiBatchPipeline.RunAsync([
            async ct => { (await Service().ExecuteAsync(new("Alpha.mkv"), 1, ct)).Success.Should().BeTrue(); await Task.Delay(250, ct); },
            async ct => { (await Service().ExecuteAsync(new("Beta.mkv"), 2, ct)).Success.Should().BeTrue(); }
        ], new());
    }
    private sealed class LimitedQuota : IAiProviderQuotaTracker, IAiProviderQuotaReservationTracker
    {
        public int Calls, Tokens;
        public Task<AiProviderQuotaReservation?> TryReserveCallAsync(long providerId, CancellationToken ct = default)
        { if (Calls >= 1) return Task.FromResult<AiProviderQuotaReservation?>(null); Calls++; return Task.FromResult<AiProviderQuotaReservation?>(new(AiQuotaPeriod.None, null)); }
        public Task SettleTokensAsync(long providerId, AiProviderQuotaReservation reservation, int? promptTokens, int? completionTokens, CancellationToken ct = default)
        { Tokens += (promptTokens ?? 0) + (completionTokens ?? 0); return Task.CompletedTask; }
        public Task RecordUsageAsync(long providerId, int? promptTokens, int? completionTokens, CancellationToken ct = default)
        { Calls++; return SettleTokensAsync(providerId, new(AiQuotaPeriod.None, null), promptTokens, completionTokens, ct); }
    }
    private sealed class SplitParser : IAiParser, IAiPhysicalRequestParser
    {
        public int Sent;
        public bool Supports(AiProviderType protocol) => true;
        public async Task<AiParseOutcome> ParseAsync(AiProviderType protocol, AiProviderEndpoint endpoint, AiParseRequest request, CancellationToken ct = default)
        {
            for (int n = 0; n < 2; n++)
            {
                int index = await AiTransportScope.Current!.StartedAsync(ct);
                Sent++;
                await AiTransportScope.Current.CompletedAsync(index, 5, 2);
            }
            return new AiParseOutcome(new("Alpha", null, "movie", null, null, null, 0.9));
        }
    }

    private sealed class Parser : IAiParser, IAiPhysicalRequestParser
    {
        public int Calls;
        public Action? AfterSend { get; init; }
        public bool Supports(AiProviderType protocol) => true;
        public async Task<AiParseOutcome> ParseAsync(AiProviderType protocol, AiProviderEndpoint endpoint, AiParseRequest request, CancellationToken ct = default)
        {
            AiTransportScope transport = AiTransportScope.Current!;
            try { return await AiBatchPipeline.Schedule("fixture", transport, Batch, ct, transport.ResponseReady)!; }
            finally { transport.ResponseReleased(); }
        }
        private async Task<IReadOnlyDictionary<string, AiParseOutcome>> Batch(IReadOnlyList<AiBatchEntry<AiTransportScope>> entries, CancellationToken _)
        {
            Calls++;
            int index = await entries[0].Input.StartedAsync(_);
            await entries[0].Input.CompletedAsync(index, 20, 5);
            AfterSend?.Invoke();
            foreach (AiBatchEntry<AiTransportScope> entry in entries) entry.ResponseReady?.Invoke();
            return entries.ToDictionary(e => e.Id,
                _ => new AiParseOutcome(new("Example", null, "movie", null, null, null, 0.9)));
        }
    }
}
