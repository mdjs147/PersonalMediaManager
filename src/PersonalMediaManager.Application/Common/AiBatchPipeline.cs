using PersonalMediaManager.Application.Common.Diagnostics;
namespace PersonalMediaManager.Application.Common;

/// <summary>单项独立边界与取消信号</summary>
public sealed record AiBatchEntry<T>(string Id, T Input, CancellationToken CancellationToken, Action? ResponseReady = null, Func<bool>? TryTakeFallback = null, Action? TerminalReady = null);

/// <summary>仅在 AI 等待点让出执行权的串行管线</summary>
/// <remarks>准备、落库、归档和清理逐项执行。推理结果先存储，由调度器逐项放行；取消回调不得直接触发业务续行。</remarks>
public static class AiBatchPipeline
{
    private static readonly AsyncLocal<Slot?> Current = new();
    public static Task<TResult>? Schedule<TInput, TResult>(string compatibilityKey, TInput input,
        Func<IReadOnlyList<AiBatchEntry<TInput>>, CancellationToken, Task<IReadOnlyDictionary<string, TResult>>> execute,
        CancellationToken ct, Action? responseReady = null, int inputBytes = 0, int maxBatchItems = 128)
    {
        Slot? slot = Current.Value;
        if (slot is null) return null;
        if (inputBytes is < 0 or > AiBatchOptions.MaxItemInputBytes)
            throw new ArgumentException("AI 单项准备输入超出上限", nameof(inputBytes));
        if (maxBatchItems is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxBatchItems));
        Operation<TInput, TResult> operation = new(compatibilityKey, input, execute, ct, responseReady, inputBytes, maxBatchItems, slot.FallbackBudget.TryTake, slot.TimeProvider);
        slot.Waiting = operation;
        slot.Paused.TrySetResult();
        return operation.Completion.Task;
    }
    public static Task RunAsync(IReadOnlyList<Func<CancellationToken, Task>> jobs,
        AiBatchOptions options, CancellationToken ct = default) => RunAsync(jobs, options, TimeProvider.System, ct);

    internal static async Task RunAsync(IReadOnlyList<Func<CancellationToken, Task>> jobs,
        AiBatchOptions options, TimeProvider timeProvider, CancellationToken ct = default)
    {
        options.Validate();
        if (jobs.Count > options.MaxItems) throw new ArgumentException("批次项数超出上限", nameof(jobs));
        List<Slot> slots = [];
        FallbackBudget fallbackBudget = new();
        long waveMemory = GC.GetTotalMemory(false);
        try
        {
            foreach (Func<CancellationToken, Task> job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                Slot slot = new() { FallbackBudget = fallbackBudget, TimeProvider = timeProvider };
                slots.Add(slot);
                slot.Task = StartAsync(slot, job, ct);
                await AwaitPauseAsync(slot);
                await ResumeReadyAsync();
                while (await FlushAsync(onlyFull: true)) await ResumeReadyAsync();
                long preparedBytes = slots.Sum(s => (long)(s.Waiting?.InputBytes ?? 0));
                if (preparedBytes >= AiBatchOptions.MaxPendingInputBytes
                    || GC.GetTotalMemory(false) - waveMemory >= AiBatchOptions.MaxPreparedManagedGrowthBytes)
                {
                    ParseDiagnostics.Emit("ai.batch_preparation_pressure", new { PreparedItems = slots.Count,
                        PreparedBytes = preparedBytes, ManagedGrowthBytes = GC.GetTotalMemory(false) - waveMemory });
                    await DrainAsync();
                    slots.Clear();
                    waveMemory = GC.GetTotalMemory(false);
                }
            }
            await DrainAsync();
        }
        finally
        {
            // 异常和停机也逐项释放，并等待每项的 finally 完成。
            foreach (Slot slot in slots.Where(s => !s.Task.IsCompleted))
            {
                while (!slot.Task.IsCompleted)
                {
                    if (slot.Waiting is Operation operation)
                    {
                        slot.Waiting = null;
                        slot.Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        operation.Abort();
                    }
                    await Task.WhenAny(slot.Task, slot.Paused.Task);
                }
            }
            foreach (Slot slot in slots) { try { await slot.Task; } catch { /* 原异常由上层保留。 */ } }
        }
        async Task DrainAsync()
        {
            while (slots.Any(s => !s.Task.IsCompleted))
            {
                await FlushAsync();
                await ResumeReadyAsync();
            }
            foreach (Slot slot in slots) await slot.Task;
        }
        async Task ResumeReadyAsync()
        {
            // 当前准备项已经暂停，优先释放已完成的小批，不能为凑外部128饿死本地2项预算。
            foreach (Slot slot in slots.Where(s => !s.Task.IsCompleted))
            {
                Operation? operation = slot.Waiting;
                if (operation is null || !operation.Executed) continue;
                slot.Waiting = null;
                slot.Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
                operation.Release();
                await AwaitPauseAsync(slot);
            }
        }
        async Task AwaitPauseAsync(Slot active)
        {
            while (!active.Task.IsCompleted && !active.Paused.Task.IsCompleted)
            {
                Operation[] pending = slots.Select(s => s.Waiting).OfType<Operation>().Where(o => !o.Executed).ToArray();
                if (pending.Length == 0) { await Task.WhenAny(active.Task, active.Paused.Task); continue; }
                double oldest = pending.Max(o => o.Wait.TotalMilliseconds);
                Task timer = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, options.MaxWaitMilliseconds - oldest)), timeProvider, ct);
                Task ready = await Task.WhenAny(active.Task, active.Paused.Task, timer);
                if (ready == timer) { ct.ThrowIfCancellationRequested(); await FlushAsync(); }
            }
            if (active.Task.IsCompleted) await active.Task;
        }
        async Task<bool> FlushAsync(bool onlyFull = false)
        {
            Operation[] pending = slots.Select(s => s.Waiting).OfType<Operation>().Where(o => !o.Executed).ToArray();
            Operation[][] groups = pending.OrderBy(o => o.EnqueuedAt).GroupBy(o => (o.GetType(), o.Key)).Select(g => g.ToArray()).ToArray();
            if (onlyFull)
            {
                if (!groups.Any(group => group.Length >= group.Min(o => o.MaxBatchItems))) return false;
                // 有后到满组时仍先服务最早等待的部分组，先放行结果，再准备更多项。
                await groups[0][0].ExecuteAsync(groups[0], ct);
                return true;
            }
            foreach (Operation[] batch in groups) await batch[0].ExecuteAsync(batch, ct);
            return groups.Length > 0;
        }
    }
    private static async Task StartAsync(Slot slot, Func<CancellationToken, Task> job, CancellationToken ct)
    {
        Slot? previous = Current.Value;
        Current.Value = slot;
        try { await job(ct); }
        finally { Current.Value = previous; }
    }
    private sealed class FallbackBudget
    {
        private int _used;
        public bool TryTake()
        {
            int old;
            do { old = Volatile.Read(ref _used); if (old >= AiBatchOptions.MaxAutomaticFallbackRequests) return false; }
            while (Interlocked.CompareExchange(ref _used, old + 1, old) != old);
            return true;
        }
    }
    private sealed class Slot
    {
        public FallbackBudget FallbackBudget { get; init; } = new();
        public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
        public Task Task { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Paused { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Operation? Waiting { get; set; }
    }
    private abstract class Operation(string key, CancellationToken ct, Action? responseReady, int inputBytes, int maxBatchItems, TimeProvider timeProvider)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Key { get; } = key;
        public int InputBytes { get; } = inputBytes;
        public int MaxBatchItems { get; } = maxBatchItems;
        public CancellationToken Token { get; } = ct;
        // 入队、计时与唤醒共用单调时钟，避免测试依赖宿主的墙钟调度。
        public long EnqueuedAt { get; } = timeProvider.GetTimestamp();
        public TimeSpan Wait => timeProvider.GetElapsedTime(EnqueuedAt);
        protected bool Terminal { get; private set; }
        public void FinishTerminal() { Terminal = true; FinishResponse(); }
        public Action<string, object?> Emit { get; } = ParseDiagnostics.CaptureEmitter();
        public bool Executed { get; protected set; }
        private int _ready;
        public void FinishResponse()
        {
            if (Interlocked.Exchange(ref _ready, 1) == 0) responseReady?.Invoke();
        }
        public abstract Task ExecuteAsync(Operation[] batch, CancellationToken ct);
        public abstract void Release();
        public abstract void Abort();
    }
    private sealed class Operation<TInput, TResult>(string key, TInput input,
        Func<IReadOnlyList<AiBatchEntry<TInput>>, CancellationToken, Task<IReadOnlyDictionary<string, TResult>>> execute,
        CancellationToken ct, Action? responseReady, int inputBytes, int maxBatchItems, Func<bool> tryTakeFallback, TimeProvider timeProvider) : Operation(key, ct, responseReady, inputBytes, maxBatchItems, timeProvider)
    {
        public TaskCompletionSource<TResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TResult? _result;
        private Exception? _error;
        private readonly TInput _input = input;
        private readonly Func<bool> _tryTakeFallback = tryTakeFallback;
        public override async Task ExecuteAsync(Operation[] batch, CancellationToken cancellation)
        {
            Operation<TInput, TResult>[] all = batch.Cast<Operation<TInput, TResult>>().ToArray();
            Operation<TInput, TResult>[] active = all.Where(o => !o.Token.IsCancellationRequested).ToArray();
            using CancellationTokenSource shared = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            CancellationTokenRegistration[] registrations = active.Select(o => o.Token.Register(() =>
            { if (active.All(x => x.Token.IsCancellationRequested)) shared.Cancel(); })).ToArray();
            try
            {
                if (active.Length == 0) return;
                string batchId = Guid.NewGuid().ToString("N");
                foreach (Operation<TInput, TResult> operation in active)
                    operation.Emit("ai.batch_scheduled", new { BatchId = batchId, ItemId = operation.Id,
                        ItemCount = active.Length, FirstItemWaitMs = active.Max(o => (long)o.Wait.TotalMilliseconds) });
                IReadOnlyDictionary<string, TResult> results = await execute(active.Select(o => new AiBatchEntry<TInput>(o.Id, o._input, o.Token, o.FinishResponse, o._tryTakeFallback, o.FinishTerminal)).ToArray(), shared.Token);
                foreach (Operation<TInput, TResult> operation in active)
                    if (results.TryGetValue(operation.Id, out TResult? result)) operation._result = result;
                    else operation._error = new InvalidOperationException("AI 批次未返回指定项");
            }
            catch (Exception ex) { foreach (Operation<TInput, TResult> operation in active) operation._error = ex; }
            finally
            {
                foreach (CancellationTokenRegistration registration in registrations) registration.Dispose();
                foreach (Operation<TInput, TResult> operation in all) operation.Executed = true;
            }
        }
        public override void Release()
        {
            if (Token.IsCancellationRequested && !Terminal) Completion.TrySetCanceled(Token);
            else if (_error is not null) Completion.TrySetException(_error);
            else Completion.TrySetResult(_result!);
        }
        public override void Abort() => Completion.TrySetCanceled(new CancellationToken(true));
    }
}
