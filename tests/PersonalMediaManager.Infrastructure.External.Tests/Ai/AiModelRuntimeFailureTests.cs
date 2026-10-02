using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.DependencyInjection;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;
using PersonalMediaManager.Infrastructure.External.Ai.Protocols;
using PersonalMediaManager.Infrastructure.External.Tests.Tmdb;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class AiModelRuntimeFailureTests
{
    [Theory]
    [InlineData(500, "check_tensor_dims: wrong shape", 1, "ModelRuntime", true)]
    [InlineData(500, "check_tensor_dims: wrong shape", 1, "ModelRuntime", false)]
    [InlineData(500, "GGML_ASSERT(x) failed", 1, "ModelRuntime", true)]
    [InlineData(500, "unknown server failure", 2, "Transient", true)]
    [InlineData(503, "temporary overload", 2, "Transient", true)]
    [InlineData(503, "temporary overload", 2, "Transient", false)]
    public async Task RealProtocolsThroughOrchestrator_BoundedRetryThenFallback(int status, string error, int requests, string category, bool structured)
    {
        StubHttpMessageHandler primary = new();
        for (int i = 0; i < requests; i++)
            primary.EnqueueResponse((HttpStatusCode)status, JsonSerializer.Serialize(new { error }));
        StubHttpMessageHandler backup = new();
        backup.EnqueueResponse(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"title\":\"Example\",\"type\":\"movie\",\"confidence\":0.9}"}}]}""");
        AiProtocolParser parser = new([
            new OllamaProtocol(new StubHttpClientFactory(primary), NullLogger<OllamaProtocol>.Instance,
                new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50))),
            new OpenAiCompatibleProtocol(new StubHttpClientFactory(backup), NullLogger<OpenAiCompatibleProtocol>.Instance,
                new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50))),
        ]);
        ChainServices recording = new();
        ServiceCollection services = new();
        services.AddApplication();
        services.AddLogging();
        services.AddSingleton<IAiParser>(parser);
        services.AddSingleton<IAiProviderResolver>(recording);
        services.AddSingleton<IAuditAiCallWriter>(recording);
        services.AddSingleton<IAiProviderHealthTracker>(recording);
        services.AddSingleton<IAiProviderQuotaTracker>(recording);
        services.AddSingleton<IAiProviderRpmGate>(recording);
        using ServiceProvider container = services.BuildServiceProvider();
        using IServiceScope scope = container.CreateScope();
        IAiCallOrchestrator orchestrator = scope.ServiceProvider.GetRequiredService<IAiCallOrchestrator>();
        AiParseRequest request = new("Example.mkv", Context: structured ? new() : null);
        AiCallOutcome result = await orchestrator.ExecuteAsync(request, null);
        result.WinningProviderId.Should().Be(2);
        primary.Requests.Should().HaveCount(requests);
        backup.Requests.Should().ContainSingle();
        recording.Audits.Should().HaveCount(2, "audit rows remain provider-level, not wire-attempt tracing");
        recording.Audits[0].ErrorType.Should().Be(category);
        recording.Health.Should().Equal(1L);
        if (structured)
            result.RequestMetadata.Should().NotBeNull();
        else
        {
            result.RequestMetadata.Should().BeNull();
            string legacyPrompt = AiPromptHelpers.BuildUserPrompt(request);
            recording.Audits.Should().OnlyContain(a => a.RequestText == legacyPrompt);
        }
    }

    [Theory]
    [InlineData("model loading failed: check_tensor_dims: wrong shape")]
    [InlineData("tensor dimensions shape mismatch")]
    [InlineData("runner exited: GGML_ASSERT(a->ne[0] == b->ne[0]) failed")]
    public async Task OllamaKnownRuntimeSignature_MapsWithoutLeakingBodyInMessage(string error)
    {
        string body = JsonSerializer.Serialize(new { error });
        StubHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, body);
        OllamaProtocol protocol = new(new StubHttpClientFactory(handler), NullLogger<OllamaProtocol>.Instance,
            new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));
        Func<Task> call = () => protocol.CompleteAsync(new AiProtocolRequest(
            new("https://synthetic.example", null, "synthetic-model", IsFree: true), [new("user", "sample")], JsonMode: true));
        FluentAssertions.Specialized.ExceptionAssertions<AiProviderModelRuntimeException> thrown = await call.Should().ThrowAsync<AiProviderModelRuntimeException>();
        thrown.Which.Data[AiCallDiagnostics.HttpStatusKey].Should().Be(500);
        thrown.Which.Data[AiCallDiagnostics.ResponseTextKey].Should().Be(body);
        thrown.Which.Message.Should().NotContain(error);
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Ollama", 500, "{\"error\":\"runner stopped\"}")]
    [InlineData("Ollama", 500, "{\"error\":\"temporary overload\"}")]
    [InlineData("Ollama", 503, "{\"error\":\"GGML_ASSERT failure\"}")]
    [InlineData("OpenAiCompatible", 500, "{\"error\":\"GGML_ASSERT failure\"}")]
    [InlineData("Ollama", 500, "{\"message\":\"GGML_ASSERT failure\"}")]
    [InlineData("Ollama", 500, "malformed GGML_ASSERT")]
    public void UnknownOrOtherProtocolFailure_RemainsTransient(string protocol, int status, string body)
    {
        Action call = () => AiHttpFailureMapper.ThrowForStatus(protocol, (HttpStatusCode)status, body);
        call.Should().Throw<AiProviderTransientException>();
    }

    [Fact]
    public void OversizedError_RemainsTransient_BoundedClassification()
    {
        string body = JsonSerializer.Serialize(new { error = "GGML_ASSERT " + new string('x', 20_000) });
        Action call = () => AiHttpFailureMapper.ThrowForStatus("Ollama", HttpStatusCode.InternalServerError, body);
        call.Should().Throw<AiProviderTransientException>();
    }
    private sealed class ChainServices : IAiProviderResolver, IAuditAiCallWriter, IAiProviderHealthTracker,
        IAiProviderQuotaTracker, IAiProviderRpmGate
    {
        public List<AuditAiCallEntry> Audits { get; } = [];
        public List<long> Health { get; } = [];
        public Task<IReadOnlyList<AiProviderResolution>> ResolveOrderedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiProviderResolution>>([
                new(1, AiProviderType.Ollama, "primary", true, new("https://synthetic.example", null, "model-a", IsFree: true)),
                new(2, AiProviderType.OpenAiCompatible, "backup", false, new("https://synthetic.example", null, "model-b", IsFree: true)),
            ]);
        public Task WriteAsync(AuditAiCallEntry entry, CancellationToken ct = default)
        {
            Audits.Add(entry);
            return Task.CompletedTask;
        }
        public Task EvaluateAsync(long providerId, CancellationToken ct = default)
        {
            Health.Add(providerId);
            return Task.CompletedTask;
        }
        public Task RecordUsageAsync(long providerId, int? promptTokens, int? completionTokens, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsThrottled(long providerId, int? rpmLimit) => false;
        public void Record(long providerId) { }
    }

}
