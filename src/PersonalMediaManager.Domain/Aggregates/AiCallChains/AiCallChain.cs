using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Aggregates.AiCallChains;

/// <summary>AI 兜底调用链聚合（非持久化）— 多级升级 + 瞬时错误 1 次重试不计</summary>
/// <remarks>
/// D1.5：把「调用次数上限 + 重试豁免」的不变式集中到此聚合，AiCallOrchestrator（D3.3）只问 CanCallNext / RecordXxx。
/// 规则（需求文档 §3.3.3 + 多级升级扩展）：
/// - 链路对单个文件最多 <see cref="HardCap"/> 次外部 AI 调用（= 当前可用 provider 数，由构造传入；
///   越靠前优先级越高，逐级升级到「更高级」AI）。绝对天花板 <see cref="MaxHardCap"/> 做成本护栏。
/// - 每级失败（接口异常 / 4xx / 限流 429 / 结果置信度不达标）→ 升级到下一级。
/// - 瞬时错误（DNS / TCP 握手失败 / 首字节超时 &lt; 5s）允许内部 1 次短重试（500ms 退避），不计入级数额度。
/// - 限流 / 逻辑错误 / 低置信度不重试，直接消耗一级额度并升级。
/// - 任一级返回「可接受」结果后立即停止；全部级别耗尽 → 链路结束 → SendToReview。
/// 本聚合不入库，仅在单次解析任务期间存活；Audit_AiCall 表记录历史细节。
/// </remarks>
public sealed class AiCallChain
{
    /// <summary>默认链路硬上限（未显式指定可用 provider 数时）：主 + 备 + 1 个更高级，共 3 级</summary>
    public const int DefaultHardCap = 3;

    /// <summary>链路硬上限的绝对天花板（成本护栏：无论配多少 provider，单文件最多升级到此级数）</summary>
    public const int MaxHardCap = 10;

    /// <summary>同提供商内瞬时错误重试上限：1 次</summary>
    public const int TransientRetryLimitPerProvider = 1;

    private readonly int _hardCap;
    private int _providersCalled;
    private int _transientRetriesOnCurrentProvider;
    private bool _completedSuccessfully;

    /// <summary>构造一条调用链；hardCap = 本次可升级的最大级数（默认 <see cref="DefaultHardCap"/>，钳到 [1, <see cref="MaxHardCap"/>]）</summary>
    public AiCallChain(int hardCap = DefaultHardCap)
    {
        _hardCap = Math.Clamp(hardCap, 1, MaxHardCap);
    }

    /// <summary>本链路允许的最大级数（外部 AI 调用次数上限）</summary>
    public int HardCap => _hardCap;

    /// <summary>已消耗的级数（0~HardCap）</summary>
    public int ProvidersCalled => _providersCalled;

    /// <summary>当前 provider 已用的瞬时重试次数（0~1）</summary>
    public int TransientRetriesOnCurrent => _transientRetriesOnCurrentProvider;

    /// <summary>链路是否已成功结束（任一级返回可接受结果）</summary>
    public bool CompletedSuccessfully => _completedSuccessfully;

    /// <summary>链路是否还有升级额度（未成功 + 未到硬上限）</summary>
    public bool CanCallNextProvider => !_completedSuccessfully && _providersCalled < _hardCap;

    /// <summary>当前 provider 是否还有瞬时重试额度</summary>
    public bool CanRetryTransient => !_completedSuccessfully && _transientRetriesOnCurrentProvider < TransientRetryLimitPerProvider;

    /// <summary>开始调用下一级 provider（消耗 1 级额度，重置当前 provider 的瞬时重试计数）</summary>
    /// <remarks>
    /// 调用顺序：AiCallOrchestrator 应在 Resolver 选好下一个 provider 后调本方法。
    /// 超过硬上限抛 DomainException（防御性，正常路径应先检查 CanCallNextProvider）。
    /// </remarks>
    public void BeginProviderCall()
    {
        if (_completedSuccessfully)
            throw new DomainException("AI 调用链已成功结束，不应再开始新 provider 调用");
        if (_providersCalled >= _hardCap)
            throw new DomainException($"AI 调用链已达硬上限 {_hardCap} 级，不可再升级");

        _providersCalled += 1;
        _transientRetriesOnCurrentProvider = 0;
    }

    /// <summary>记录当前 provider 的瞬时错误（不消耗级数额度，仅消耗 provider 内重试额度）</summary>
    public void RecordTransientError()
    {
        if (_providersCalled == 0)
            throw new DomainException("尚未开始 provider 调用，不能记录瞬时错误");
        if (_transientRetriesOnCurrentProvider >= TransientRetryLimitPerProvider)
            throw new DomainException($"当前 provider 瞬时重试已达上限 {TransientRetryLimitPerProvider} 次");

        _transientRetriesOnCurrentProvider += 1;
    }

    /// <summary>记录当前 provider 调用成功（终结链路）</summary>
    public void RecordSuccess()
    {
        if (_providersCalled == 0)
            throw new DomainException("尚未开始 provider 调用，不能记录成功");
        _completedSuccessfully = true;
    }

    /// <summary>当前 provider 失败（逻辑错误 / 限流 / 低置信度 / 瞬时重试已耗尽）→ 链路准备升级到下一级</summary>
    /// <remarks>
    /// 本方法不消耗额度（额度由 BeginProviderCall 消耗）；仅清当前 provider 的瞬时重试计数，
    /// 让外层 Orchestrator 自行调 BeginProviderCall 升到下一级。
    /// </remarks>
    public void RecordProviderFailure()
    {
        if (_providersCalled == 0)
            throw new DomainException("尚未开始 provider 调用，不能记录失败");
        _transientRetriesOnCurrentProvider = 0;
    }
}
