using System.Text.Json;
using NSubstitute;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed partial class AiCallOrchestratorTests
{
    [Fact]
    public async Task DiagnosticsTrackQualityRejectionAndFallbackWithOneRunId()
    {
        AiDiagnosticSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", "quality-run", 42, sink);
        FakeProvider primary = new(AiProviderType.Ollama) { NextResults = [OkResult(0.1)] };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };
        AiCallOrchestrator sut = NewSut([primary, backup],
            [Resolution(1, AiProviderType.Ollama, true), Resolution(2, AiProviderType.Anthropic, false)],
            Substitute.For<IAuditAiCallWriter>());
        AiCallOutcome result = await sut.ExecuteAsync(SampleRequest(), 42);
        result.Success.Should().BeTrue();
        ParseDiagnosticEvent[] providers = sink.Events.Where(e => e.Name == "ai.provider_result").ToArray();
        providers.Should().HaveCount(2);
        providers[0].Data.GetProperty("errorType").GetString().Should().Be("LowConfidence");
        providers[1].Data.GetProperty("success").GetBoolean().Should().BeTrue();
        sink.Events.Should().OnlyContain(e => e.RunId == "quality-run" && e.MediaItemId == 42);
        sink.Events.Should().Contain(e => e.Name == "ai.chain_completed");
        JsonSerializer.Serialize(sink.Events).Should().NotContain("Inception.2010.mkv");
    }

    [Fact]
    public async Task DiagnosticsRecordRetryCancellationWithoutCallingBackup()
    {
        AiDiagnosticSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        FakeProvider primary = new(AiProviderType.Ollama) { AlwaysThrow = new AiProviderTransientException("private provider error") };
        FakeProvider backup = new(AiProviderType.Anthropic) { NextResults = [OkResult()] };
        AiCallOrchestrator sut = NewSut([primary, backup],
            [Resolution(1, AiProviderType.Ollama, true), Resolution(2, AiProviderType.Anthropic, false)], Substitute.For<IAuditAiCallWriter>());
        using CancellationTokenSource cancellation = new();
        Task<AiCallOutcome> pending = sut.ExecuteAsync(SampleRequest(), null, cancellation.Token);
        cancellation.Cancel();
        await ((Func<Task>)(() => pending)).Should().ThrowAsync<OperationCanceledException>();
        backup.CallCount.Should().Be(0);
        sink.Events.Should().Contain(e => e.Name == "ai.retry_scheduled");
        sink.Events.Should().Contain(e => e.Name == "ai.chain_cancelled" && e.Data.GetProperty("callerCancelled").GetBoolean());
        JsonSerializer.Serialize(sink.Events).Should().NotContain("private provider error");
    }

    [Theory]
    [InlineData(ParseDiagnosticLevel.Off, false)]
    [InlineData(ParseDiagnosticLevel.Standard, false)]
    [InlineData(ParseDiagnosticLevel.Detailed, true)]
    public async Task ProviderBodyInExceptionCannotBypassDownstreamTextPolicy(ParseDiagnosticLevel level, bool keepsBody)
    {
        const string marker = "PrivacyResponseMarker42";
        AiDiagnosticSink sink = new();
        sink.Options.Level = level;
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        AiProviderLogicalException error = new($"提供商失败：body={marker}; password=pass-secret <think>private-thought</think>", 400);
        FakeProvider primary = new(AiProviderType.Ollama) { AlwaysThrow = error };
        IAuditAiCallWriter audit = Substitute.For<IAuditAiCallWriter>();
        AiCallOrchestrator sut = NewSut([primary], [Resolution(1, AiProviderType.Ollama, true)], audit);
        AiCallOutcome outcome = await sut.ExecuteAsync(SampleRequest(), 42);
        outcome.Success.Should().BeFalse();
        outcome.Attempts!.Single().ErrorType.Should().Be("Http4xx");
        string downstream = JsonSerializer.Serialize(outcome);
        downstream.Should().NotContain("pass-secret").And.NotContain("private-thought");
        if (keepsBody) downstream.Should().Contain(marker);
        else downstream.Should().NotContain(marker).And.Contain("Http4xx");
        await audit.Received(1).WriteAsync(Arg.Is<AuditAiCallEntry>(entry =>
            entry.ErrorType == "Http4xx" && entry.ErrorDetail != null
            && entry.ErrorDetail.Contains(marker) == keepsBody
            && !entry.ErrorDetail.Contains("pass-secret") && !entry.ErrorDetail.Contains("private-thought")), Arg.Any<CancellationToken>());
    }

    private sealed class AiDiagnosticSink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }
}
