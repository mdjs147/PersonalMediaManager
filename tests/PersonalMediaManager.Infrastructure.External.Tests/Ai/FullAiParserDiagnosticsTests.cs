using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Infrastructure.External.Ai.Protocols;
using PersonalMediaManager.Infrastructure.External.Tests.Tmdb;
using PersonalMediaManager.Infrastructure.External.Tmdb;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.Ai;
using PersonalMediaManager.Infrastructure.Platform.Diagnostics;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class FullAiParserDiagnosticsTests
{
    private static readonly AiProviderEndpoint Endpoint = new("https://unused.invalid", "known-test-key", "test-model");
    [Fact]
    public async Task BatchReferencesOneSharedResponseButKeepsPerItemCleaningAndFallbackPhysicalIds()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-batch-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            FixtureProtocol protocol = new();
            AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 2 });
            string[] runs = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")];
            int requests = 0;
            await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(i => async ct =>
            {
                using IDisposable context = ParseDiagnostics.Begin("parse", runs[i], i + 1, sink);
                using IDisposable attempt = ParseDiagnostics.BeginAttempt(i + 1);
                using AiTransportScope transport = new AiTransportScope(() => requests++) { ProviderId = 7 }.Enter();
                AiParseOutcome result = await parser.ParseAsync(protocol.Protocol, Endpoint, new(i == 0 ? "Alpha.mkv" : "Beta.mkv"), ct);
                result.Result.Title.Should().Be(i == 0 ? "Alpha" : "Beta");
            }).ToArray(), new());
            protocol.Calls.Should().Be(2); requests.Should().Be(2);
            ParseReplayExport first = sink.Export(runs[0], null), second = sink.Export(runs[1], null);
            ParseDiagnosticEvent sharedA = first.Events.Single(e => e.Name == "ai.batch_response");
            ParseDiagnosticEvent sharedB = second.Events.Single(e => e.Name == "ai.batch_response");
            sharedA.RequestId.Should().Be(sharedB.RequestId);
            sharedA.Data.GetProperty("content").GetProperty("artifactId").GetString().Should().Be(sharedB.Data.GetProperty("content").GetProperty("artifactId").GetString());
            first.Events.Single(e => e.Name == "ai.parse_result").ItemId.Should().NotBeNull();
            second.Events.Single(e => e.Name == "ai.parse_result").RequestId.Should().NotBe(sharedB.RequestId);
            second.Events.Single(e => e.Name == "ai.parse_result").BatchId.Should().Be(sharedB.BatchId);
            first.Events.Where(e => e.RequestId is not null).Should().OnlyContain(e => e.Attempt == 1);
            second.Events.Where(e => e.RequestId is not null).Should().OnlyContain(e => e.Attempt == 2);
            first.Artifacts.Should().OnlyContain(a => a.State == "recorded");
            second.Artifacts.Should().OnlyContain(a => a.State == "recorded");
            first.Events.Where(e => e.Name == "ai.cleanup" && e.Data.GetProperty("stage").GetString() == "batch_item_json_extraction")
                .Should().ContainSingle();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BrokenStorageCannotRetryOrChangeParseOutcome()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-failed-storage-");
        try
        {
            string path = Path.Combine(root, "not-a-directory"); File.WriteAllText(path, "keep");
            using ParseDiagnosticFileSink sink = new(path, new() { Level = ParseDiagnosticLevel.Full });
            FixtureProtocol protocol = new(); AiProtocolParser parser = new([protocol]);
            using IDisposable context = ParseDiagnostics.Begin("parse", mediaItemId: 1, sink: sink);
            AiParseOutcome result = await parser.ParseAsync(protocol.Protocol, Endpoint, new("Beta.mkv"));
            result.Result.Title.Should().Be("Beta"); protocol.Calls.Should().Be(1);
            File.ReadAllText(path).Should().Be("keep");
        }
        finally { Directory.Delete(root, true); }
    }


    [Fact]
    public async Task QuotaDenialKeepsPreparedPromptButNeverClaimsHttpDispatch()
    {
        string root = PrivateFileSystem.CreateTemporaryDirectory("pmm-full-prepared-");
        try
        {
            using ParseDiagnosticFileSink sink = new(root, new() { Level = ParseDiagnosticLevel.Full });
            StubHttpMessageHandler handler = new();
            OpenAiCompatibleProtocol protocol = new(new StubHttpClientFactory(handler), NullLogger<OpenAiCompatibleProtocol>.Instance,
                new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));
            string run = Guid.NewGuid().ToString("N");
            using IDisposable context = ParseDiagnostics.Begin("parse", run, 1, sink);
            using AiTransportScope transport = new AiTransportScope(() => { throw new AiProviderRateLimitException("quota denied"); }).Enter();
            Func<Task> action = () => new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol, Endpoint with { IsFree = true }, new("Alpha.mkv"));
            await action.Should().ThrowAsync<AiProviderRateLimitException>();
            ParseReplayExport export = sink.Export(run, null);
            export.Events.Single(e => e.Name == "ai.request").Data.GetProperty("stage").GetString().Should().Be("prepared_not_yet_sent");
            export.Events.Should().NotContain(e => e.Name == "ai.http_request_dispatch" || e.Name == "ai.http_response");
            handler.Requests.Should().BeEmpty(); transport.Usage.Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(AiProviderType.OpenAiCompatible)]
    [InlineData(AiProviderType.AzureOpenAi)]
    [InlineData(AiProviderType.Anthropic)]
    [InlineData(AiProviderType.Gemini)]
    [InlineData(AiProviderType.Ollama)]
    public async Task CancellationDuringFullRequestCaptureCannotReserveUnsentRequest(AiProviderType type)
    {
        using CancellationTokenSource cancellation = new();
        CancelOnRequestBodySink sink = new(cancellation);
        using IDisposable context = ParseDiagnostics.Begin("parse", sink: sink);
        StubHttpMessageHandler handler = new();
        StubHttpClientFactory factory = new(handler);
        TokenBucketRateLimiter limiter = new(1000, TimeSpan.FromMilliseconds(50));
        IAiProtocol protocol = type switch
        {
            AiProviderType.OpenAiCompatible => new OpenAiCompatibleProtocol(factory, NullLogger<OpenAiCompatibleProtocol>.Instance, limiter),
            AiProviderType.AzureOpenAi => new AzureOpenAiProtocol(factory, NullLogger<AzureOpenAiProtocol>.Instance, limiter),
            AiProviderType.Anthropic => new AnthropicProtocol(factory, NullLogger<AnthropicProtocol>.Instance, limiter),
            AiProviderType.Gemini => new GeminiProtocol(factory, NullLogger<GeminiProtocol>.Instance, limiter),
            _ => new OllamaProtocol(factory, NullLogger<OllamaProtocol>.Instance, limiter),
        };
        int reserved = 0;
        AiProtocolRequest request = new(Endpoint with { IsFree = true }, [new("user", "synthetic")], JsonMode: false,
            BeforeSend: _ => { reserved++; return Task.CompletedTask; });
        Func<Task> action = () => protocol.CompleteAsync(request, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
        reserved.Should().Be(0);
    }

    [Fact]
    public async Task DispatchEvidenceIsEmittedOnlyAfterHttpSendHasStarted()
    {
        StubHttpMessageHandler handler = new();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"safe\"}}]}");
        bool observed = false, afterSend = false;
        ObservingDiagnosticSink sink = new(value =>
        {
            if (value.Name == "ai.http_request_dispatch") { observed = true; afterSend = handler.Requests.Count == 1; }
        });
        using IDisposable context = ParseDiagnostics.Begin("parse", sink: sink);
        OpenAiCompatibleProtocol protocol = new(new StubHttpClientFactory(handler), NullLogger<OpenAiCompatibleProtocol>.Instance,
            new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));
        await protocol.CompleteAsync(new(Endpoint with { IsFree = true }, [new("user", "synthetic")], JsonMode: false));
        observed.Should().BeTrue(); afterSend.Should().BeTrue();
    }

    private sealed class ObservingDiagnosticSink(Action<ParseDiagnosticEvent> observe) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = ParseDiagnosticLevel.Full };
        public void Write(ParseDiagnosticEvent value) => observe(value);
    }

    private sealed class CancelOnRequestBodySink(CancellationTokenSource cancellation) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = ParseDiagnosticLevel.Full };
        public void Write(ParseDiagnosticEvent value)
        {
            if (value.Name is "ai.http_request_prepared" or "ai.http_request_dispatch") cancellation.Cancel();
        }
    }

    private sealed class FixtureProtocol : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public int Calls { get; private set; }
        public Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (request.Messages[1].Content.StartsWith('{'))
            {
                using JsonDocument input = JsonDocument.Parse(request.Messages[1].Content);
                JsonElement first = input.RootElement.GetProperty("items")[0];
                return Task.FromResult(new AiCompletion(JsonSerializer.Serialize(new { items = new[] { new { id = first.GetProperty("id").GetString(), result = new { title = "Alpha", type = "movie", confidence = .9 } } } }), 5, 4));
            }
            return Task.FromResult(new AiCompletion("```json\n{\n \"title\" : \"Beta\", \"type\" : \"movie\", \"confidence\":0.9, \"year\":1999\n}\n```", 3, 2));
        }
    }
}
