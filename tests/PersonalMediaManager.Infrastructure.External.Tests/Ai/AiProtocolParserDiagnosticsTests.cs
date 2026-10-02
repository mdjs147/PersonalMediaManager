using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Infrastructure.External.Ai.Protocols;
using PersonalMediaManager.Infrastructure.External.Tests.Tmdb;
using PersonalMediaManager.Infrastructure.External.Tmdb;
using System.Text.Json;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

/// <summary>实际提示词、解析结果与取消的隐私诊断</summary>
public sealed class AiProtocolParserDiagnosticsTests
{
    private const string Response = """{"title":"Example","type":"movie","confidence":0.9}""";
    private static readonly AiProviderEndpoint Endpoint = new("https://private.invalid/v1?token=url-secret", "exact-endpoint-secret", "test-model");

    [Fact]
    public async Task DetailedRecordsBothActualPromptsAndPreservesParentCorrelation()
    {
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", "same-file-run", 42, sink);
        StubProtocol protocol = new(Response);
        AiProtocolParser parser = new([protocol]);
        AiParseOutcome outcome = await parser.ParseAsync(protocol.Protocol, Endpoint, new("Example.mkv"));

        outcome.Result.Title.Should().Be("Example");
        JsonElement request = sink.Event("ai.request");
        request.GetProperty("system").GetProperty("text").GetString().Should().Be(protocol.LastRequest!.Messages[0].Content);
        request.GetProperty("user").GetProperty("text").GetString().Should().Be(protocol.LastRequest.Messages[1].Content);
        request.GetProperty("maxTokens").GetInt32().Should().Be(protocol.LastRequest.MaxTokens);
        request.GetProperty("fingerprint").GetString().Should().HaveLength(64);
        sink.Event("ai.response").GetProperty("promptTokens").GetInt32().Should().Be(8);
        sink.Event("ai.parse_result").GetProperty("parsed").GetBoolean().Should().BeTrue();
        sink.Events.Should().OnlyContain(e => e.RunId == "same-file-run" && e.MediaItemId == 42);
        string all = JsonSerializer.Serialize(sink.Events);
        all.Should().NotContain("private.invalid").And.NotContain("exact-endpoint-secret").And.NotContain("url-secret");
    }

    [Fact]
    public async Task StandardRecordsHashesWithoutPromptOrResponseBodies()
    {
        Sink sink = new(ParseDiagnosticLevel.Standard);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        StubProtocol protocol = new(Response);
        await new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol, Endpoint, new("Example.mkv"));
        foreach (JsonElement text in new[]
        {
            sink.Event("ai.request").GetProperty("system"), sink.Event("ai.request").GetProperty("user"),
            sink.Event("ai.response").GetProperty("response"),
        })
        {
            text.GetProperty("state").GetString().Should().Be("not_recorded");
            text.GetProperty("text").ValueKind.Should().Be(JsonValueKind.Null);
            text.GetProperty("sha256").GetString().Should().HaveLength(64);
            text.GetProperty("originalUtf8Bytes").GetInt32().Should().BeGreaterThan(0);
        }
        JsonSerializer.Serialize(sink.Events).Should().NotContain("Example.mkv");
    }

    [Fact]
    public async Task PrivateReasoningAndUnlabelledEndpointCredentialNeverEnterEvents()
    {
        const string response = """{"title":"Example","type":"movie","confidence":0.9,"reasoning_content":"private-model-thought","note":"exact-endpoint-secret"}""";
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        StubProtocol protocol = new(response);
        AiParseOutcome result = await new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol, Endpoint, new("Example.mkv"));
        string all = JsonSerializer.Serialize(sink.Events);
        all.Should().NotContain("private-model-thought").And.NotContain("exact-endpoint-secret");
        result.ResponseText.Should().NotContain("exact-endpoint-secret");
    }

    [Fact]
    public async Task LogicalFailureRecordsParseFailureAndResponseOnce()
    {
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        StubProtocol protocol = new("invalid json");
        Func<Task> run = () => new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol, Endpoint, new("Example.mkv"));
        await run.Should().ThrowAsync<AiProviderLogicalException>();
        sink.Event("ai.parse_result").GetProperty("parsed").GetBoolean().Should().BeFalse();
        sink.Events.Count(e => e.Name == "ai.response").Should().Be(1);
    }

    [Fact]
    public async Task CancellationRemainsCancellationAndHasTerminalEvent()
    {
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        using CancellationTokenSource cancellation = new();
        StubProtocol protocol = new(Response) { Handler = (_, ct) => { cancellation.Cancel(); return Task.FromCanceled<AiCompletion>(ct); } };
        Func<Task> run = () => new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol, Endpoint, new("Example.mkv"), cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        sink.Event("ai.cancelled").GetProperty("callerCancelled").GetBoolean().Should().BeTrue();
        sink.Event("ai.cancelled").GetProperty("response").GetProperty("state").GetString().Should().Be("unknown");
        sink.Events.Should().NotContain(e => e.Name == "ai.parse_result");
    }

    [Fact]
    public async Task FailureBodyIsRedactedAndKeepsHttpStatus()
    {
        AiProviderLogicalException failure = new("bad request exact-endpoint-secret", 401);
        failure.Data[AiCallDiagnostics.HttpStatusKey] = 401;
        failure.Data[AiCallDiagnostics.ResponseTextKey] = """{"reasoning":"hidden-thought","token":"unsafe-token","detail":"exact-endpoint-secret"}""";
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        StubProtocol protocol = new(Response) { Handler = (_, _) => Task.FromException<AiCompletion>(failure) };
        Func<Task> run = () => new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol, Endpoint, new("Example.mkv"));
        await run.Should().ThrowAsync<AiProviderLogicalException>();
        sink.Event("ai.failed").GetProperty("httpStatus").GetInt32().Should().Be(401);
        JsonSerializer.Serialize(sink.Events).Should().NotContain("hidden-thought").And.NotContain("unsafe-token").And.NotContain("exact-endpoint-secret");
    }

    [Fact]
    public async Task GeminiThoughtPartsNeverReachParserOrDiagnostics()
    {
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        StubHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[]
            {
                new { thought = true, text = "private-gemini-thought" },
                new { thought = false, text = Response },
            } } } },
        }));
        GeminiProtocol protocol = new(new StubHttpClientFactory(handler), NullLogger<GeminiProtocol>.Instance,
            new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));
        AiParseOutcome outcome = await new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol,
            Endpoint with { IsFree = true }, new("Example.mkv"));
        outcome.Result.Title.Should().Be("Example");
        outcome.ResponseText.Should().Be(Response);
        JsonSerializer.Serialize(sink.Events).Should().NotContain("private-gemini-thought");
    }

    [Fact]
    public async Task AnthropicThinkingOnlyFailureCannotLeakThroughEmbeddedErrorBody()
    {
        Sink sink = new(ParseDiagnosticLevel.Detailed);
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        StubHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "thinking", thinking = "private-anthropic-thought" + new string('x', 300) } },
            stop_reason = "max_tokens",
        }));
        AnthropicProtocol protocol = new(new StubHttpClientFactory(handler), NullLogger<AnthropicProtocol>.Instance,
            new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));
        Func<Task> run = () => new AiProtocolParser([protocol]).ParseAsync(protocol.Protocol,
            Endpoint with { IsFree = true }, new("Example.mkv"));
        await run.Should().ThrowAsync<AiProviderLogicalException>();
        sink.Events.Should().Contain(e => e.Name == "ai.failed");
        JsonSerializer.Serialize(sink.Events).Should().NotContain("private-anthropic-thought");
    }

    private sealed class StubProtocol(string response) : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public AiProtocolRequest? LastRequest { get; private set; }
        public Func<AiProtocolRequest, CancellationToken, Task<AiCompletion>>? Handler { get; init; }
        public Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Handler?.Invoke(request, ct) ?? Task.FromResult(new AiCompletion(response, 8, 5));
        }
    }

    private sealed class Sink(ParseDiagnosticLevel level) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = level };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
        public JsonElement Event(string name) => Events.Single(e => e.Name == name).Data;
    }
}
