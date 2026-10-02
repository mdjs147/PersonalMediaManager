using System.Text.Json;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class LocalMediaAssistDiagnosticsTests
{
    [Theory]
    [InlineData("{\"index\":1}", "Validated")]
    [InlineData("{\"index\":999}", "Rejected")]
    public async Task DiagnosticsDescribeValidationWithNoStandardBodies(string response, string status)
    {
        Sink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", "assist-run", sink: sink);
        ILocalAiInferenceClient client = Substitute.For<ILocalAiInferenceClient>();
        client.GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocalAiInferenceResult(response, null, "stop", 17, Attempted: true));
        LocalMediaAssistResult result = await Service(client).SuggestAsync(FileParseContext.FileNameOnly("Example S01E02.mkv"), null, LocalAiMode.AfterRules);
        result.Status.Should().Be(status);
        ParseDiagnosticEvent completed = sink.Events.Single(e => e.Name == "local_ai.assist_result");
        completed.Data.GetProperty("status").GetString().Should().Be(status);
        sink.Events.Should().OnlyContain(e => e.RunId == "assist-run");
        sink.Events.Single(e => e.Name == "local_ai.response").Data.GetProperty("response").GetProperty("state").GetString().Should().Be("not_recorded");
        JsonSerializer.Serialize(sink.Events).Should().NotContain("Example S01E02.mkv");
    }

    [Fact]
    public async Task CachedResultIsMarkedWithoutPretendingToMakeAnotherInference()
    {
        Sink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        ILocalAiInferenceClient client = Substitute.For<ILocalAiInferenceClient>();
        client.GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocalAiInferenceResult("{\"index\":1}", null, "stop", Attempted: true));
        LocalMediaAssistService service = Service(client);
        FileParseContext source = FileParseContext.FileNameOnly("Example S01E02.mkv");
        await service.SuggestAsync(source, null, LocalAiMode.AfterRules);
        await service.SuggestAsync(source, null, LocalAiMode.AfterRules);
        sink.Events.Count(e => e.Name == "local_ai.response").Should().Be(1);
        JsonElement cached = sink.Events.Last(e => e.Name == "local_ai.assist_result").Data;
        cached.GetProperty("fromCache").GetBoolean().Should().BeTrue();
        cached.GetProperty("inferenceAttempted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AlreadyCancelledInputEmitsCancellationWithoutInference()
    {
        Sink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        ILocalAiInferenceClient client = Substitute.For<ILocalAiInferenceClient>();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Func<Task> run = () => Service(client).SuggestAsync(FileParseContext.FileNameOnly("Example.mkv"), null, LocalAiMode.AfterRules, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        sink.Events.Should().Contain(e => e.Name == "local_ai.assist_cancelled");
        await client.DidNotReceive().GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiagnosticFingerprintNeverUsesPrivateRuntimePathCacheKey()
    {
        Sink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        ILocalAiInferenceClient client = Substitute.For<ILocalAiInferenceClient>();
        client.GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocalAiInferenceResult("{\"index\":1}", null, "stop", Attempted: true));
        ILocalAiSettingsService settings = Substitute.For<ILocalAiSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new LocalAiSettingsDto { Mode = LocalAiMode.AfterRules, RuntimeExecutablePath = "/home/private-a/llama-server" },
            new LocalAiSettingsDto { Mode = LocalAiMode.AfterRules, RuntimeExecutablePath = "/home/private-b/llama-server" });
        LocalMediaAssistService service = new(settings, client, Substitute.For<ITmdbSearchService>(), new(), new SystemClock());
        FileParseContext source = FileParseContext.FileNameOnly("Example S01E02.mkv");
        await service.SuggestAsync(source, null, LocalAiMode.AfterRules);
        await service.SuggestAsync(source, null, LocalAiMode.AfterRules);
        JsonElement[] requests = sink.Events.Where(e => e.Name == "local_ai.request").Select(e => e.Data).ToArray();
        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(request => !request.GetProperty("cacheHit").GetBoolean());
        requests[0].GetProperty("fingerprint").GetString().Should().Be(requests[1].GetProperty("fingerprint").GetString());
        JsonSerializer.Serialize(sink.Events).Should().NotContain("private-a").And.NotContain("private-b");
    }

    private static LocalMediaAssistService Service(ILocalAiInferenceClient client)
    {
        ILocalAiSettingsService settings = Substitute.For<ILocalAiSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto { Mode = LocalAiMode.AfterRules });
        return new(settings, client, Substitute.For<ITmdbSearchService>(), new(), new SystemClock());
    }

    private sealed class Sink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }
}
