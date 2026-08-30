using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.AiCallChains;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>AI 兜底调用编排实现（D3.3）— 多级升级</summary>
/// <remarks>
/// 实现要点：
/// - 升级序列 = Resolver 输出的全部可用 provider；级数上限 = provider 数（AiCallChain 构造传入，钳到 MaxHardCap）
/// - 解析经 IAiParser 门面（按协议路由到 IAiProtocol + 拼提示词/反解）；协议无实现时记 Audit + 进下一级
/// - 升级触发条件（逐级而上，越靠后越「高级」）：
///     · 瞬时错误（AiProviderTransientException）：若 AiCallChain.CanRetryTransient → Task.Delay(500ms) + 重试一次；再失败 → 升级
///     · 限流（AiProviderRateLimitException）：本级达上限，不重试，直接升级（errorType=RateLimit）
///     · 逻辑错误（AiProviderLogicalException）：直接升级（errorType=Http4xx/Http5xx/Logical）
///     · 结果不满意（!result.IsAcceptable(threshold)）：无条件升级（errorType=LowConfidence）
/// - 成功且结果可接受：写 Audit(success=true) + RecordSuccess + 返回
/// - 健康追踪（D3.4）：仅「接口故障」（Transient/RateLimit/Http*/Logical）失败后调 EvaluateAsync；
///   LowConfidence 不触发（provider 本身健康，只是结果不够好，不应被自动熔断禁用）
/// - 套餐配额计量：每处 Audit 写入后并列调 <see cref="IAiProviderQuotaTracker.RecordUsageAsync"/>，
///   成功失败都计（失败请求也可能计费，保守保护钱包）、token 与审计行同值；成功路径 HealthTracker 不评估但配额必计量
/// - Resolver 返回空：ProvidersAttempted=0，Success=false，FailureSummary="未配置可用 AI 提供商"
///
/// Audit 写入是 fire-and-await（不吞异常）；LowConfidence 也写 Audit（success=false, errorType=LowConfidence），
/// 供监控页统计升级次数，但 HealthTracker 已排除该类型，不会误自动禁用。
/// </remarks>
internal sealed class AiCallOrchestrator : IAiCallOrchestrator
{
    /// <summary>瞬时错误内部重试退避（需求文档 §3.3.3）</summary>
    internal static readonly TimeSpan TransientRetryBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>整条调用链顶层超时（r2 P2-r2.5）：兜底某个 provider hang 死也不会让单次 Parse 永远阻塞</summary>
    /// <remarks>
    /// 单 provider 自身已通过 HttpClient.Timeout（= 各自 TimeoutSeconds）限时；此处再加一层链路总超时兜底。
    /// 链路要逐级串行尝试，故总预算 = 各级 provider 自身超时之和 + 每级升级开销（<see cref="ChainPerLevelOverhead"/>，
    /// 覆盖瞬时重试 1 次 + 退避 500ms + 解析反解余量），再钳到 [<see cref="ChainMinTimeout"/>, <see cref="ChainMaxTimeout"/>]。
    /// 之所以按各 provider 超时累加（而非旧的「基础 30s + 每级 15s」固定公式）：本地模型如 Ollama 首次加载可能数分钟，
    /// 用户会把该 provider 的 TimeoutSeconds 调大（DTO 上限 600s）；若链路总超时仍按固定公式封顶 120s，
    /// 这个大超时还没等到就被链路层砍断，等于配了白配。累加后每一级都能等满它自己的超时；
    /// <see cref="ChainMaxTimeout"/> 仅作「配置爆炸」绝对护栏（如配满 10 个超大超时 provider）。
    /// 触发后（审计修复项 2）：先给「正在等待的当级 provider」补写一条 ErrorType=Timeout 的 Audit_AiCall 行
    /// （否则挂死的那一级反而无任何审计痕迹，最需要诊断时查不到），再抛出 message 中文化的
    /// OperationCanceledException（含超时秒数与当前提供商），由 ProcessFileService 捕获走兜底分支，
    /// MarkFailed 的失败原因对用户可读。
    /// </remarks>
    internal static readonly TimeSpan ChainPerLevelOverhead = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ChainMinTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ChainMaxTimeout = TimeSpan.FromSeconds(900);

    /// <summary>仅测试用：覆盖链路总超时（单测不必真实等待 30s 计时窗）</summary>
    internal TimeSpan? ChainTimeoutOverride { get; init; }

    /// <summary>升级原因分类常量（写 Audit_AiCall.ErrorType + AiCallAttempt.ErrorType）</summary>
    internal const string ErrorTransient = "Transient";
    internal const string ErrorRateLimit = "RateLimit";
    internal const string ErrorLogical = "Logical";
    internal const string ErrorHttp4xx = "Http4xx";
    internal const string ErrorHttp5xx = "Http5xx";
    internal const string ErrorLowConfidence = "LowConfidence";
    internal const string ErrorConfigError = "ConfigError";
    internal const string ErrorTimeout = "Timeout";

    private readonly IAiProviderResolver _resolver;
    private readonly IAiParser _parser;
    private readonly IAuditAiCallWriter _audit;
    private readonly IAiProviderHealthTracker _healthTracker;
    private readonly IAiProviderQuotaTracker _quotaTracker;
    private readonly IAiProviderRpmGate _rpmGate;
    private readonly ILogger<AiCallOrchestrator> _logger;
    private readonly IAlertService? _alert;

    public AiCallOrchestrator(
        IAiProviderResolver resolver,
        IAiParser parser,
        IAuditAiCallWriter audit,
        IAiProviderHealthTracker healthTracker,
        IAiProviderQuotaTracker quotaTracker,
        IAiProviderRpmGate rpmGate,
        ILogger<AiCallOrchestrator> logger,
        IAlertService? alert = null)
    {
        _resolver = resolver;
        _parser = parser;
        _audit = audit;
        _healthTracker = healthTracker;
        _quotaTracker = quotaTracker;
        _rpmGate = rpmGate;
        _logger = logger;
        _alert = alert;
    }

    public async Task<AiCallOutcome> ExecuteAsync(AiParseRequest request, long? mediaItemId, CancellationToken ct = default)
    {
        IReadOnlyList<AiProviderResolution> ordered = await _resolver.ResolveOrderedAsync(ct);
        if (ordered.Count == 0)
        {
            _logger.LogWarning("AI 调用链：无可用 provider（resolver 返回空）");
            // AI 全挂（未配置 / 全部冷却）→ 主动告警（全局键 + 抑制窗口，避免批量解析时每文件一条）
            if (_alert is not null)
                await _alert.RaiseAsync(WebhookEvents.AiAllUnavailable, WebhookEvents.AiAllUnavailable,
                    new { mediaItemId, reason = "已启用的 AI 提供商全部不可用（未配置或全部处于冷却）" }, ct);
            return new AiCallOutcome(Success: false, Result: null, WinningProviderId: null,
                ProvidersAttempted: 0, FailureSummary: "未配置可用 AI 提供商", Attempts: []);
        }

        // r2 P2-r2.5：链路顶层超时 = 各级 provider 自身超时之和 + 每级开销，钳 [Min, Max]（详见字段 remarks）；
        // linked CTS 让外层 ct 取消（请求中断）+ 计时器任一触发都终止
        int chainLevels = Math.Min(ordered.Count, AiCallChain.MaxHardCap);
        double budgetSeconds = 0;
        for (int i = 0; i < chainLevels; i++)
            budgetSeconds += Math.Max(1, ordered[i].Endpoint.TimeoutSeconds) + ChainPerLevelOverhead.TotalSeconds;
        TimeSpan chainTimeout = TimeSpan.FromSeconds(budgetSeconds);
        if (chainTimeout < ChainMinTimeout) chainTimeout = ChainMinTimeout;
        if (chainTimeout > ChainMaxTimeout) chainTimeout = ChainMaxTimeout;
        if (ChainTimeoutOverride is not null) chainTimeout = ChainTimeoutOverride.Value;   // 仅测试注入，生产恒为 null
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(chainTimeout);
        CancellationToken linkedCt = linkedCts.Token;

        // 级数上限 = 可用 provider 数（AiCallChain 内部再钳到 MaxHardCap 成本护栏）
        AiCallChain chain = new(ordered.Count);
        // 升级链关联 Id：本次解析所有级别写入的 Audit_AiCall 行共享，供监控页还原「主→备→更高级」轨迹
        string chainId = Guid.NewGuid().ToString("N");
        List<string> attemptSummaries = [];
        List<AiCallAttempt> attempts = [];
        int level = 0;

        foreach (AiProviderResolution res in ordered)
        {
            if (!chain.CanCallNextProvider) break;
            level++;

            if (!_parser.Supports(res.Type))
            {
                // 配置漂移：DB 里的协议找不到对应实现（理论上不发生，DI 注册时即覆盖全枚举）
                await _audit.WriteAsync(new AuditAiCallEntry(
                    res.ProviderId, mediaItemId, Success: false, LatencyMs: 0,
                    ErrorType: ErrorConfigError, ErrorDetail: $"无 IAiProtocol 实现：{res.Type}",
                    Model: res.Endpoint.Model, ChainId: chainId, AttemptLevel: level, IsPrimary: res.IsPrimary), ct);
                // 套餐配额计量与审计行同口径（无 token 信息只计次数）
                await _quotaTracker.RecordUsageAsync(res.ProviderId, promptTokens: null, completionTokens: null, ct);
                attemptSummaries.Add($"{res.Name}({res.Type}): 无实现");
                attempts.Add(new AiCallAttempt(level, res.ProviderId, res.Name, res.Type, res.IsPrimary,
                    Success: false, Confidence: null, ErrorType: ErrorConfigError, ErrorDetail: $"无 IAiProtocol 实现：{res.Type}", LatencyMs: 0));
                // 配置漂移不调 BeginProviderCall（不消耗级数额度），但保留递增的 level 作为轨迹序号（更直观）
                continue;
            }

            // 本地 RPM 滑动窗口限流：本 provider 最近 60 秒请求数已达 RpmLimit → 跳过本级（不发 HTTP、不消耗级数额度、不计配额、不触发健康评估），
            // 记一条 Audit + attempt 作轨迹，直接升级到下一级；窗口滑出后自动恢复（与上方「配置漂移」同款不消耗级数）
            if (_rpmGate.IsThrottled(res.ProviderId, res.RpmLimit))
            {
                await _audit.WriteAsync(new AuditAiCallEntry(
                    res.ProviderId, mediaItemId, Success: false, LatencyMs: 0,
                    ErrorType: ErrorRateLimit,
                    ErrorDetail: $"本地 RPM 限流（≤{res.RpmLimit}/分钟），跳过本级升级到下一级",
                    Model: res.Endpoint.Model, ChainId: chainId, AttemptLevel: level, IsPrimary: res.IsPrimary), ct);
                attemptSummaries.Add($"{res.Name}({res.Type}): RPM 限流跳过");
                attempts.Add(new AiCallAttempt(level, res.ProviderId, res.Name, res.Type, res.IsPrimary,
                    Success: false, Confidence: null, ErrorType: ErrorRateLimit,
                    ErrorDetail: $"本地 RPM 限流（≤{res.RpmLimit}/分钟）", LatencyMs: 0));
                continue;
            }

            chain.BeginProviderCall();
            // 登记一次实际发起的请求（滑动窗口计数 +1）：在真正调用前记，与限流判定同口径
            _rpmGate.Record(res.ProviderId);
            ProviderCallOutcome call;
            Stopwatch levelSw = Stopwatch.StartNew();
            try
            {
                call = await CallWithTransientRetryAsync(res, request, chain, linkedCt);
            }
            catch (OperationCanceledException oce) when (linkedCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // 链路总超时（非外部取消，外部取消时 ct 已请求、走原样上抛）：此前挂死的当级 provider 没有任何
                // Audit 行（TaskCanceledException 不是 AiProvider*Exception，直接穿透 CallWithTransientRetryAsync），
                // 最需要诊断「哪家 provider 挂死」时恰好查不到（审计修复项 2）——补写一条 ErrorType=Timeout 的
                // 审计行再抛。审计写入用原始 ct（linkedCt 已取消写不进库）；异常 message 中文化，
                // 让下游 ProcessFileService.MarkFailed 落库的失败原因可读。
                levelSw.Stop();
                await _audit.WriteAsync(new AuditAiCallEntry(
                    res.ProviderId, mediaItemId, Success: false, LatencyMs: (int)levelSw.Elapsed.TotalMilliseconds,
                    ErrorType: ErrorTimeout,
                    ErrorDetail: $"AI 调用链路总超时（{chainTimeout.TotalSeconds:F0} 秒），第 {level} 级 provider「{res.Name}」已等待 {levelSw.Elapsed.TotalSeconds:F1} 秒仍未返回",
                    Model: res.Endpoint.Model, ChainId: chainId, AttemptLevel: level, IsPrimary: res.IsPrimary), ct);
                // 挂死的请求已实际发出（可能计费）：套餐配额同样计量（用原始 ct，与审计写入同口径）
                await _quotaTracker.RecordUsageAsync(res.ProviderId, promptTokens: null, completionTokens: null, ct);
                throw new OperationCanceledException(
                    $"AI 调用链路超时（{chainTimeout.TotalSeconds:F0} 秒），当前提供商：{res.Name}", oce, linkedCt);
            }
            bool success = call.Success;
            AiParseResult? result = call.Result;
            string? errorType = call.ErrorType;
            string? errorDetail = call.ErrorDetail;
            int latencyMs = call.LatencyMs;
            double? confidence = result?.Confidence;
            int? httpStatus = call.HttpStatus;

            // 质量门：HTTP 成功但置信度不达标 → 视为「结果不满意」软失败，升级到下一级（落实「反馈不满意无条件升级」）
            if (success && result is not null && !result.IsAcceptable(res.ConfidenceThreshold))
            {
                success = false;
                errorType = ErrorLowConfidence;
                errorDetail = $"置信度 {result.Confidence:F2} < 阈值 {res.ConfidenceThreshold:F2}，升级到更高级 AI";
            }

            // Audit 写入用原始 ct（即使链路超时也要落审计行）；含 token / 置信度 / 原文 / 升级链等监控维度
            await _audit.WriteAsync(new AuditAiCallEntry(
                res.ProviderId, mediaItemId, success, latencyMs, errorType, errorDetail,
                Model: res.Endpoint.Model,
                PromptTokens: call.PromptTokens,
                CompletionTokens: call.CompletionTokens,
                Confidence: confidence,
                HttpStatus: httpStatus,
                ChainId: chainId,
                AttemptLevel: level,
                IsPrimary: res.IsPrimary,
                RequestText: call.RequestText,
                ResponseText: call.ResponseText), ct);

            // 套餐配额计量：成功/失败/低置信都计（实际 HTTP 调用已发出），token 与上面审计行同值；
            // 成功路径 HealthTracker 不评估但配额必须计量
            await _quotaTracker.RecordUsageAsync(res.ProviderId, call.PromptTokens, call.CompletionTokens, ct);

            if (success && result is not null)
            {
                chain.RecordSuccess();
                attempts.Add(new AiCallAttempt(level, res.ProviderId, res.Name, res.Type, res.IsPrimary,
                    Success: true, Confidence: result.Confidence, ErrorType: null, ErrorDetail: null, LatencyMs: latencyMs));
                return new AiCallOutcome(true, result, res.ProviderId, chain.ProvidersCalled, FailureSummary: null, Attempts: attempts);
            }

            // 失败 → 升级。仅「接口故障」触发健康追踪自动禁用评估；LowConfidence 不触发（provider 健康，只是结果不够好）
            if (errorType != ErrorLowConfidence)
                await _healthTracker.EvaluateAsync(res.ProviderId, ct);

            chain.RecordProviderFailure();
            attemptSummaries.Add($"{res.Name}({res.Type}): {errorType} {errorDetail}");
            attempts.Add(new AiCallAttempt(level, res.ProviderId, res.Name, res.Type, res.IsPrimary,
                Success: false, Confidence: result?.Confidence, ErrorType: errorType, ErrorDetail: errorDetail, LatencyMs: latencyMs));
        }

        string summary = $"AI 升级链耗尽（{chain.ProvidersCalled}/{chain.HardCap} 级）：{string.Join(" | ", attemptSummaries)}";
        return new AiCallOutcome(false, null, null, chain.ProvidersCalled, summary, Attempts: attempts);
    }

    /// <summary>对单个 provider：直接调；遇瞬时错误按 AiCallChain 额度内部重试 1 次；限流 / 逻辑错误直接失败（交上层升级）</summary>
    private async Task<ProviderCallOutcome> CallWithTransientRetryAsync(
        AiProviderResolution res, AiParseRequest request, AiCallChain chain, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                AiParseOutcome o = await _parser.ParseAsync(res.Type, res.Endpoint, request, ct);
                sw.Stop();
                // 成功：HTTP 状态记 200（解析门面成功即 2xx），携带原文 + token 供监控
                return new ProviderCallOutcome(true, o.Result, null, null, (int)sw.Elapsed.TotalMilliseconds,
                    o.PromptTokens, o.CompletionTokens, 200, o.RequestText, o.ResponseText);
            }
            catch (AiProviderTransientException ex)
            {
                if (chain.CanRetryTransient)
                {
                    chain.RecordTransientError();
                    _logger.LogWarning(ex, "AI provider {Provider} 瞬时错误，{Backoff}ms 后重试", res.Name, TransientRetryBackoff.TotalMilliseconds);
                    await Task.Delay(TransientRetryBackoff, ct);
                    continue;
                }
                sw.Stop();
                return FailureOutcome(ErrorTransient, ex, (int)sw.Elapsed.TotalMilliseconds);
            }
            catch (AiProviderRateLimitException ex)
            {
                // 限流 / 配额：本 provider 已达上限，不重试，直接升级到更高级 AI（落实「接口达到上限自动升级」）
                sw.Stop();
                return FailureOutcome(ErrorRateLimit, ex, (int)sw.Elapsed.TotalMilliseconds);
            }
            catch (AiProviderLogicalException ex)
            {
                sw.Stop();
                string errorType = ex.HttpStatus switch
                {
                    >= 400 and < 500 => ErrorHttp4xx,
                    >= 500 => ErrorHttp5xx,
                    _ => ErrorLogical,
                };
                return FailureOutcome(errorType, ex, (int)sw.Elapsed.TotalMilliseconds);
            }
        }
    }

    /// <summary>从契约异常的 Exception.Data 读出诊断原文/状态码/token，组装失败结果（见 AiCallDiagnostics）</summary>
    private static ProviderCallOutcome FailureOutcome(string errorType, Exception ex, int latencyMs) =>
        new(Success: false, Result: null, errorType, ex.Message, latencyMs,
            PromptTokens: ex.Data[AiCallDiagnostics.PromptTokensKey] as int?,
            CompletionTokens: ex.Data[AiCallDiagnostics.CompletionTokensKey] as int?,
            HttpStatus: ex.Data[AiCallDiagnostics.HttpStatusKey] as int?,
            RequestText: ex.Data[AiCallDiagnostics.RequestTextKey] as string,
            ResponseText: ex.Data[AiCallDiagnostics.ResponseTextKey] as string);

    /// <summary>单个 provider 一次调用的产出（成功结果或失败诊断 + token/原文/状态码）</summary>
    private sealed record ProviderCallOutcome(
        bool Success,
        AiParseResult? Result,
        string? ErrorType,
        string? ErrorDetail,
        int LatencyMs,
        int? PromptTokens,
        int? CompletionTokens,
        int? HttpStatus,
        string? RequestText,
        string? ResponseText);
}
