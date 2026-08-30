using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Host.HostedServices;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>TaskProcessorWorker（D6.2）— 严格串行 + 异常不断 + 优雅取消 + FIFO</summary>
/// <remarks>
/// 用 RecordingProcessFileService 替身记录调用顺序 / 并发数；
/// 不引真实 DbContext，因为 Worker 本身只做调度，业务在 ProcessFileService 里（D7.1）。
/// </remarks>
public sealed class TaskProcessorWorkerTests
{
    [Fact]
    public async Task Strictly_Serial_Even_Under_Bursty_Enqueue()
    {
        RecordingProcessFileService svc = new(processingDelayMs: 200);
        PendingFileQueue queue = new();
        TaskProcessorWorker sut = NewWorker(queue, svc);

        await sut.StartAsync(CancellationToken.None);
        for (int i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(NewItem($"/tmp/{i}.mkv"));
        }

        await WaitUntil(() => svc.CompletedCount == 5);
        await sut.StopAsync(CancellationToken.None);

        svc.MaxConcurrent.Should().Be(1, "Semaphore(1,1) 必须保证任何时刻最多 1 个文件在 ProcessAsync");
        svc.CompletedCount.Should().Be(5);
    }

    [Fact]
    public async Task FIFO_Order_Preserved()
    {
        RecordingProcessFileService svc = new(processingDelayMs: 50);
        PendingFileQueue queue = new();
        TaskProcessorWorker sut = NewWorker(queue, svc);

        await sut.StartAsync(CancellationToken.None);
        string[] paths = ["/tmp/a.mkv", "/tmp/b.mkv", "/tmp/c.mkv"];
        foreach (string p in paths)
        {
            await queue.EnqueueAsync(NewItem(p));
        }

        await WaitUntil(() => svc.CompletedCount == 3);
        await sut.StopAsync(CancellationToken.None);

        svc.ProcessedPaths.Should().Equal(paths, "Channel + Semaphore 必须保证 FIFO");
    }

    [Fact]
    public async Task Exception_From_ProcessAsync_Does_Not_Stop_Worker()
    {
        ThrowingProcessFileService svc = new(failOnPath: "/tmp/bad.mkv");
        PendingFileQueue queue = new();
        TaskProcessorWorker sut = NewWorker(queue, svc);

        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(NewItem("/tmp/good1.mkv"));
        await queue.EnqueueAsync(NewItem("/tmp/bad.mkv"));
        await queue.EnqueueAsync(NewItem("/tmp/good2.mkv"));

        await WaitUntil(() => svc.AttemptedCount == 3);
        await sut.StopAsync(CancellationToken.None);

        svc.AttemptedCount.Should().Be(3, "失败的中间项不应阻断后续处理");
        svc.AttemptedPaths.Should().Equal("/tmp/good1.mkv", "/tmp/bad.mkv", "/tmp/good2.mkv");
    }

    [Fact]
    public async Task StopAsync_Cancels_Pending_Reads_Cleanly()
    {
        RecordingProcessFileService svc = new(processingDelayMs: 50);
        PendingFileQueue queue = new();
        TaskProcessorWorker sut = NewWorker(queue, svc);

        await sut.StartAsync(CancellationToken.None);
        // 不入队任何项；Worker 应在 ReadAllAsync 上空转等待
        await Task.Delay(100);

        Func<Task> stop = async () => await sut.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync("空 channel 上 StopAsync 应通过 stoppingToken 优雅返回");
        svc.CompletedCount.Should().Be(0);
    }

    [Fact]
    public async Task Items_Enqueued_During_Processing_Are_Picked_Up_Next()
    {
        RecordingProcessFileService svc = new(processingDelayMs: 100);
        PendingFileQueue queue = new();
        TaskProcessorWorker sut = NewWorker(queue, svc);

        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(NewItem("/tmp/first.mkv"));
        await Task.Delay(30); // 让第一个进入处理
        await queue.EnqueueAsync(NewItem("/tmp/second.mkv"));
        await queue.EnqueueAsync(NewItem("/tmp/third.mkv"));

        await WaitUntil(() => svc.CompletedCount == 3);
        await sut.StopAsync(CancellationToken.None);

        svc.ProcessedPaths.Should().Equal("/tmp/first.mkv", "/tmp/second.mkv", "/tmp/third.mkv");
    }

    [Fact]
    public async Task UserCancellation_Cancels_Only_Active_Item()
    {
        CancellableProcessFileService svc = new();
        PendingFileQueue queue = new();
        TaskCancellationManager cancellation = new();
        TaskProcessorWorker sut = NewWorker(queue, svc, cancellation);
        const string path = "/tmp/cancel-me.mkv";

        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(NewItem(path));
        await svc.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task? completion = cancellation.RequestCancellation(path);
        completion.Should().NotBeNull("正在处理项应暴露退出等待任务");
        await completion!.WaitAsync(TimeSpan.FromSeconds(3));

        svc.WasCancelled.Should().BeTrue();
        await sut.StopAsync(CancellationToken.None);
    }

    private static PendingFileItem NewItem(string path)
        => new(path, WatchFolderId: 1, PendingFileSource.Watcher);

    private static TaskProcessorWorker NewWorker(
        IPendingFileQueue queue,
        IProcessFileService svc,
        ITaskCancellationManager? cancellation = null)
    {
        ServiceCollection services = new();
        services.AddScoped<IProcessFileService>(_ => svc);
        ServiceProvider sp = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new TaskProcessorWorker(queue, cancellation ?? new TaskCancellationManager(), scopeFactory,
            NullLogger<TaskProcessorWorker>.Instance);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 5000)
    {
        int waited = 0;
        while (!predicate() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }

    private sealed class RecordingProcessFileService : IProcessFileService
    {
        private readonly int _delayMs;
        private int _inflight;
        private int _maxConcurrent;
        private int _completed;
        private readonly List<string> _processed = new();
        private readonly object _lock = new();

        public RecordingProcessFileService(int processingDelayMs) { _delayMs = processingDelayMs; }
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public int CompletedCount => Volatile.Read(ref _completed);
        public IReadOnlyList<string> ProcessedPaths
        {
            get { lock (_lock) { return _processed.ToArray(); } }
        }

        public async Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _inflight);
            int oldMax;
            do
            {
                oldMax = Volatile.Read(ref _maxConcurrent);
                if (now <= oldMax) break;
            } while (Interlocked.CompareExchange(ref _maxConcurrent, now, oldMax) != oldMax);

            try
            {
                await Task.Delay(_delayMs, cancellationToken);
                lock (_lock) { _processed.Add(item.FullPath); }
                Interlocked.Increment(ref _completed);
                return new ProcessFileOutcome(1, ProcessOutcome.Completed);
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
        }
    }

    private sealed class ThrowingProcessFileService : IProcessFileService
    {
        private readonly string _failPath;
        private int _attempted;
        private readonly List<string> _attemptedPaths = new();
        private readonly object _lock = new();

        public ThrowingProcessFileService(string failOnPath) { _failPath = failOnPath; }
        public int AttemptedCount => Volatile.Read(ref _attempted);
        public IReadOnlyList<string> AttemptedPaths
        {
            get { lock (_lock) { return _attemptedPaths.ToArray(); } }
        }

        public Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempted);
            lock (_lock) { _attemptedPaths.Add(item.FullPath); }
            if (item.FullPath == _failPath)
            {
                throw new InvalidOperationException("模拟业务异常");
            }
            return Task.FromResult(new ProcessFileOutcome(1, ProcessOutcome.Completed));
        }
    }

    private sealed class CancellableProcessFileService : IProcessFileService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }

        public async Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("取消测试不应自然完成");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
