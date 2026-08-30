namespace PersonalMediaManager.Application.Common;

/// <summary>按源文件路径协调「排队 / 正在处理」任务的用户取消请求。</summary>
/// <remarks>
/// Channel 本身不支持删除指定元素：排队任务先在数据库落 Cancelled，之后消费者读到时按终态幂等跳过；
/// 正在处理的任务由本管理器取消它独立的 linked token，等待处理函数退出后再由 HistoryService 落终态。
/// </remarks>
public interface ITaskCancellationManager
{
    TaskCancellationRegistration Register(string sourcePath, CancellationToken stoppingToken);

    /// <summary>登记用户取消并取消当前活动令牌；返回活动处理结束任务，未开始处理则返回 null。</summary>
    Task? RequestCancellation(string sourcePath);

    /// <summary>清除尚未被消费者领取的取消标记。</summary>
    void ClearRequest(string sourcePath);
}

public sealed class TaskCancellationRegistration : IDisposable
{
    private readonly TaskCancellationManager _owner;
    private readonly string _key;
    private int _disposed;

    internal TaskCancellationRegistration(TaskCancellationManager owner, string key, CancellationToken token)
    {
        _owner = owner;
        _key = key;
        Token = token;
    }

    public CancellationToken Token { get; }

    public bool IsCancellationRequested => Token.IsCancellationRequested;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.Complete(_key);
    }
}

/// <summary>线程安全的进程内单例实现；路径比较遵循 Windows 大小写不敏感语义。</summary>
public sealed class TaskCancellationManager : ITaskCancellationManager
{
    private sealed class State
    {
        public bool Requested { get; set; }
        public CancellationTokenSource? ActiveTokenSource { get; set; }
        public TaskCompletionSource? Completion { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public TaskCancellationRegistration Register(string sourcePath, CancellationToken stoppingToken)
    {
        string key = Normalize(sourcePath);
        CancellationTokenSource tokenSource;
        bool cancelImmediately;

        lock (_gate)
        {
            if (!_states.TryGetValue(key, out State? state))
            {
                state = new State();
                _states.Add(key, state);
            }
            if (state.ActiveTokenSource is not null)
                throw new InvalidOperationException($"文件任务已在处理中：{sourcePath}");

            tokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            state.ActiveTokenSource = tokenSource;
            state.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancelImmediately = state.Requested;
        }

        if (cancelImmediately)
            tokenSource.Cancel();

        return new TaskCancellationRegistration(this, key, tokenSource.Token);
    }

    public Task? RequestCancellation(string sourcePath)
    {
        string key = Normalize(sourcePath);
        CancellationTokenSource? active;
        Task? completion;

        lock (_gate)
        {
            if (!_states.TryGetValue(key, out State? state))
            {
                state = new State();
                _states.Add(key, state);
            }

            state.Requested = true;
            active = state.ActiveTokenSource;
            completion = state.Completion?.Task;
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 活动处理可能恰好在锁释放后完成并释放令牌；调用方随后会复核数据库终态。
        }
        return completion;
    }

    public void ClearRequest(string sourcePath)
    {
        string key = Normalize(sourcePath);
        lock (_gate)
        {
            if (_states.TryGetValue(key, out State? state) && state.ActiveTokenSource is null)
                _states.Remove(key);
        }
    }

    internal void Complete(string key)
    {
        CancellationTokenSource? tokenSource = null;
        TaskCompletionSource? completion = null;

        lock (_gate)
        {
            if (_states.Remove(key, out State? state))
            {
                tokenSource = state.ActiveTokenSource;
                completion = state.Completion;
            }
        }

        tokenSource?.Dispose();
        completion?.TrySetResult();
    }

    private static string Normalize(string sourcePath) => Path.GetFullPath(sourcePath);
}
