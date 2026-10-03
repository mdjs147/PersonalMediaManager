using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
namespace PersonalMediaManager.Application.Tests.Parse;
public sealed class AiBatchPipelineTests
{
    [Fact]
    public async Task OlderPartialLocalGroupFinishesBeforeYoungerFullExternalGroup()
    {
        int localCalls = 0, finished = 0;
        Task<IReadOnlyDictionary<string, int>> Local(IReadOnlyList<AiBatchEntry<int>> entries, CancellationToken _)
        {
            localCalls++;
            foreach (AiBatchEntry<int> entry in entries) entry.ResponseReady?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<string, int>>(entries.ToDictionary(e => e.Id, e => e.Input));
        }
        async Task<IReadOnlyDictionary<string, int>> External(IReadOnlyList<AiBatchEntry<int>> entries, CancellationToken ct)
        {
            await Task.Delay(250, ct);
            return entries.ToDictionary(e => e.Id, e => e.Input);
        }
        async Task LocalJob(CancellationToken ct)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(100);
            await AiBatchPipeline.Schedule("local", 1, Local, deadline.Token,
                () => deadline.CancelAfter(Timeout.InfiniteTimeSpan), maxBatchItems: 2)!;
            finished++;
        }
        async Task ExternalJob(CancellationToken ct) { await AiBatchPipeline.Schedule("external", 1, External, ct, maxBatchItems: 2)!; finished++; }
        await AiBatchPipeline.RunAsync([LocalJob, ExternalJob, ExternalJob, LocalJob], new() { MaxItems = 128, MaxWaitMilliseconds = 1000 });
        finished.Should().Be(4); localCalls.Should().Be(2);
    }

    [Fact]
    public async Task RepeatedSuspensionsKeepBusinessStagesSerialAndDiagnosticsIsolated()
    {
        int running = 0, peak = 0, calls = 0, disposed = 0;
        List<string> finished = [];
        Sink sink = new();
        async Task Stage() { peak = Math.Max(peak, Interlocked.Increment(ref running)); await Task.Delay(3); Interlocked.Decrement(ref running); }
        Task<IReadOnlyDictionary<string, int>> Batch(IReadOnlyList<AiBatchEntry<int>> input, CancellationToken _)
        { calls++; return Task.FromResult<IReadOnlyDictionary<string, int>>(input.ToDictionary(e => e.Id, e => e.Input + 1)); }
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 3).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        {
            using IDisposable scope = ParseDiagnostics.Begin("parse", $"run-{i}", i + 1, sink);
            try
            {
                await Stage();
                int first = await AiBatchPipeline.Schedule("same", i, Batch, ct)!;
                await Stage();
                int second = await AiBatchPipeline.Schedule("same", first, Batch, ct)!;
                second.Should().Be(i + 2);
                await Stage(); finished.Add($"run-{i}");
                ParseDiagnostics.CurrentRunId.Should().Be($"run-{i}");
            }
            finally { disposed++; }
        }).ToArray(), new() { MaxWaitMilliseconds = 1000 });
        calls.Should().Be(2); peak.Should().Be(1); disposed.Should().Be(3);
        finished.Should().Equal("run-0", "run-1", "run-2");
        sink.Events.Where(e => e.Name == "ai.batch_scheduled").Should().HaveCount(6);
        sink.Events.Should().OnlyContain(e => e.RunId == $"run-{e.MediaItemId - 1}");
        ParseDiagnostics.CurrentRunId.Should().BeNull();
    }
    [Fact]
    public async Task SlowPreparationDoesNotHoldReadyRequestPastCollectionDeadline()
    {
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool resumed = false;
        Task<IReadOnlyDictionary<string, int>> Batch(IReadOnlyList<AiBatchEntry<int>> input, CancellationToken _)
        { sent.TrySetResult(); return Task.FromResult<IReadOnlyDictionary<string, int>>(input.ToDictionary(e => e.Id, e => e.Input)); }
        Task run = AiBatchPipeline.RunAsync([
            async ct => { await AiBatchPipeline.Schedule("x", 1, Batch, ct)!; resumed = true; },
            async _ => { slow.TrySetResult(); await release.Task; resumed.Should().BeFalse(); }
        ], new() { MaxWaitMilliseconds = 20 });
        await slow.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        resumed.Should().BeFalse();
        release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(2));
        resumed.Should().BeTrue();
    }
    [Fact]
    public async Task ShutdownReleasesEveryPausedSlotExactlyOnce()
    {
        int disposed = 0;
        using CancellationTokenSource stop = new();
        Task<IReadOnlyDictionary<string, int>> Batch(IReadOnlyList<AiBatchEntry<int>> _, CancellationToken ct)
        { stop.Cancel(); return Task.FromCanceled<IReadOnlyDictionary<string, int>>(ct); }
        Func<Task> run = () => AiBatchPipeline.RunAsync(Enumerable.Range(0, 3).Select<int, Func<CancellationToken, Task>>(_ => async ct =>
        { try { await AiBatchPipeline.Schedule("x", 1, Batch, ct)!; } finally { disposed++; } }).ToArray(), new(), stop.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        disposed.Should().Be(3);
        AiBatchPipeline.Schedule("outside", 1, Batch, default).Should().BeNull();
    }
    [Theory]
    [InlineData("{\"items\":[{\"id\":\"a\",\"result\":{}},{\"id\":\"a\",\"result\":{}}]}")]
    [InlineData("{\"items\":[{\"id\":\"new\",\"result\":{}}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"id\":\"b\",\"result\":{}}]}")]
    public void BadIdSetNeverReturnsPartialMapping(string raw)
    { Action parse = () => AiBatchJson.Parse(raw, ["a", "b"]); parse.Should().Throw<FormatException>(); }
    private sealed class Sink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) { lock (Events) Events.Add(value); }
    }
}
