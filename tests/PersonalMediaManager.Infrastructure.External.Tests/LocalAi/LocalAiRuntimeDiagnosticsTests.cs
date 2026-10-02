using System.Net;
using System.Text.Json;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.Tests.LocalAi;

public sealed partial class LocalAiRuntimeManagerTests
{
    [Fact]
    public async Task RuntimeDiagnosticsCaptureEffectiveRequestAndOnlyAssistantContent()
    {
        RuntimeDiagnosticSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", "local-run", 42, sink);
        _completion = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"index\":0}","reasoning_content":"private-local-thought"}}]}"""),
        });
        await _manager.StartAsync();
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("实际系统指令", "实际用户数据", 32, 2));
        result.Success.Should().BeTrue();
        JsonElement request = sink.Events.Single(e => e.Name == "local_ai.runtime_request").Data;
        request.GetProperty("catalogArtifactSha256").GetString().Should().HaveLength(64);
        request.GetProperty("artifactVerification").GetString().Should().Be("catalog_artifact_verified_at_startup");
        request.GetProperty("maxTokens").GetInt32().Should().Be(32);
        request.GetProperty("system").GetProperty("text").GetString().Should().Be("实际系统指令");
        request.GetProperty("user").GetProperty("text").GetString().Should().Be("实际用户数据");
        request.GetProperty("responseFormat").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        sink.Events.Should().OnlyContain(e => e.RunId == "local-run" && e.MediaItemId == 42);
        string all = JsonSerializer.Serialize(sink.Events);
        all.Should().NotContain("private-local-thought").And.NotContain(_directory);
    }

    [Fact]
    public async Task RuntimeDiagnosticsMarkCancellationAndDoNotTurnItIntoSuccess()
    {
        RuntimeDiagnosticSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Completion();
        };
        await _manager.StartAsync();
        Task<LocalAiInferenceResult> pending = _manager.GenerateAsync(new("指令", "数据"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await ((Func<Task>)(() => pending)).Should().ThrowAsync<OperationCanceledException>();
        sink.Events.Should().Contain(e => e.Name == "local_ai.runtime_cancelled" && e.Data.GetProperty("attempted").GetBoolean());
        sink.Events.Should().NotContain(e => e.Name == "local_ai.runtime_completed");
    }

    [Fact]
    public async Task RuntimeDiagnosticsShowTruncatedResponseWithoutAcceptingIt()
    {
        RuntimeDiagnosticSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        _finish = "length";
        await _manager.StartAsync();
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据", 32, 1));
        result.Success.Should().BeFalse();
        sink.Events.Should().Contain(e => e.Name == "local_ai.runtime_response"
            && e.Data.GetProperty("finishReason").GetProperty("text").GetString() == "length");
        sink.Events.Should().Contain(e => e.Name == "local_ai.runtime_completed" && e.Data.GetProperty("failureReason").GetString() == "truncated");
    }

    [Fact]
    public async Task RuntimeFingerprintUsesOnlyRedactedPromptHashes()
    {
        RuntimeDiagnosticSink sink = new();
        using IDisposable scope = ParseDiagnostics.Begin("file", sink: sink);
        await _manager.StartAsync();
        await _manager.GenerateAsync(new("指令", "数据 <think>private-first-value</think>"));
        await _manager.GenerateAsync(new("指令", "数据 <think>private-second-value</think>"));
        JsonElement[] requests = sink.Events.Where(e => e.Name == "local_ai.runtime_request").Select(e => e.Data).ToArray();
        requests.Should().HaveCount(2);
        requests[0].GetProperty("fingerprint").GetString().Should().Be(requests[1].GetProperty("fingerprint").GetString());
        JsonSerializer.Serialize(sink.Events).Should().NotContain("private-first-value").And.NotContain("private-second-value");
    }

    private sealed class RuntimeDiagnosticSink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = ParseDiagnosticLevel.Detailed };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }
}
