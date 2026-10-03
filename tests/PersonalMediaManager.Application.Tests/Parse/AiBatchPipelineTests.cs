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
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(25)]
    public async Task SlowPreparationDoesNotHoldReadyRequestPastCollectionDeadline(int preparationMilliseconds)
    {
        bool sent = false, slow = false, resumed = false, checkedBeforeDeadline = false;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AdvancingTimeProvider clock = new();
        clock.BeforeTimerFires = () =>
        {
            checkedBeforeDeadline = true;
            clock.GetElapsedTime(0).Should().Be(TimeSpan.FromMilliseconds(20) - TimeSpan.FromTicks(1));
            sent.Should().BeFalse("收集期限未到，不应提前发送");
            slow.Should().BeTrue();
            release.Task.IsCompleted.Should().BeFalse();
        };
        Task<IReadOnlyDictionary<string, int>> Batch(IReadOnlyList<AiBatchEntry<int>> input, CancellationToken _)
        {
            clock.GetElapsedTime(0).Should().Be(TimeSpan.FromMilliseconds(Math.Max(20, preparationMilliseconds)));
            release.Task.IsCompleted.Should().BeFalse("慢准备仍未完成，到期请求也必须发送");
            sent = true;
            return Task.FromResult<IReadOnlyDictionary<string, int>>(input.ToDictionary(e => e.Id, e => e.Input));
        }
        Task run = AiBatchPipeline.RunAsync([
            async ct => { await AiBatchPipeline.Schedule("x", 1, Batch, ct)!; resumed = true; },
            async _ =>
            {
                slow = true;
                clock.Advance(TimeSpan.FromMilliseconds(preparationMilliseconds));
                await release.Task;
                resumed.Should().BeFalse("准备和已完成请求的业务续行必须串行");
            }
        ], new() { MaxWaitMilliseconds = 20 }, clock);
        try
        {
            slow.Should().BeTrue();
            sent.Should().BeTrue("虚拟时钟已到收集期限，不能等待慢准备释放");
            checkedBeforeDeadline.Should().Be(preparationMilliseconds < 20);
            resumed.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await run;
        }
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
    // 同步推进虚拟定时器，让期限断言不与线程池、xUnit 上下文和 CI 负载竞速。
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public Action? BeforeTimerFires { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            period.Should().Be(Timeout.InfiniteTimeSpan);
            dueTime.Should().BeGreaterThan(TimeSpan.Zero);
            Advance(dueTime - TimeSpan.FromTicks(1));
            BeforeTimerFires?.Invoke();
            Advance(TimeSpan.FromTicks(1));
            callback(state);
            return new CompletedTimer();
        }
        private sealed class CompletedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("测试定时器不支持重新排期");
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Sink : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new();
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) { lock (Events) Events.Add(value); }
    }
}
