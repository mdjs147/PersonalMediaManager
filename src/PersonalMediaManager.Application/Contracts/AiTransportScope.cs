namespace PersonalMediaManager.Application.Contracts;

/// <summary>解析器按物理请求报告计量</summary>
public interface IAiPhysicalRequestParser { }
/// <summary>不含正文的物理请求用量</summary>
public sealed record AiTransportUsage(int? PromptTokens = null, int? CompletionTokens = null);

/// <summary>逐项调用链的传输计量上下文</summary>
public sealed class AiTransportScope : IDisposable
{
    private static readonly AsyncLocal<AiTransportScope?> Ambient = new();
    private readonly AiTransportScope? _previous = Ambient.Value;
    private readonly List<AiTransportUsage> _usage = [];
    private readonly HashSet<int> _settled = [];
    private readonly Func<CancellationToken, Task> _requestStarted;
    private readonly Func<AiTransportUsage, Task>? _requestCompleted;
    private bool _deferred;
    public AiTransportScope(Action requestStarted) : this(_ => { requestStarted(); return Task.CompletedTask; }) { }
    public AiTransportScope(Func<CancellationToken, Task> requestStarted, Func<AiTransportUsage, Task>? requestCompleted = null)
    { _requestStarted = requestStarted; _requestCompleted = requestCompleted; }
    public static AiTransportScope? Current => Ambient.Value;
    public IReadOnlyList<AiTransportUsage> Usage => _usage;
    public CancellationToken? CallerCancellationToken { get; init; }
    public long? ProviderId { get; init; }
    public Func<int, AiTransportUsage, Task>? OnSettling { get; init; }
    public Action? OnResponseReady { get; init; }
    public Action? OnResponseReleased { get; init; }
    public AiTransportScope Enter() { Ambient.Value = this; return this; }
    public async Task<int> StartedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _requestStarted(ct);
        _usage.Add(new());
        return _usage.Count - 1;
    }
    public async Task CompletedAsync(int index, int? prompt, int? completion)
    {
        if (_settled.Contains(index)) return;
        _usage[index] = new(prompt is >= 0 ? prompt : null, completion is >= 0 ? completion : null);
        await SettleAsync(index);
    }
    public async Task SettleAsync(int index)
    {
        // 写入结果不明时不能盲重试，否则可能重复累计 token。
        if (!_settled.Add(index)) return;
        if (OnSettling is not null) await OnSettling(index, _usage[index]);
        if (_requestCompleted is not null) await _requestCompleted(_usage[index]);
    }
    public async Task SettlePendingAsync()
    { for (int i = 0; i < _usage.Count; i++) await SettleAsync(i); }
    public void ResponseReady()
    { if (!_deferred) { _deferred = true; OnResponseReady?.Invoke(); } }
    public void ResponseReleased()
    { if (_deferred) { _deferred = false; OnResponseReleased?.Invoke(); } }
    public void Dispose() { ResponseReleased(); Ambient.Value = _previous; }
}
