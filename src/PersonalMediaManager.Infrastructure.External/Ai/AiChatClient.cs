using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>通用 AI 对话客户端实现（自由 prompt → 原始文本）</summary>
/// <remarks>
/// 复用 IAiProviderResolver 拿有序 provider（已解密 ApiKey + 主备 + 代理开关 + 协议），主→备遍历，首个成功即返回。
/// wire 统一委托协议策略 <see cref="IAiProtocol"/>（按 res.Type 路由），不再内联各协议报文（与解析路径同一套实现，避免漂移）。
/// 链路总超时 = 各级 provider 自身超时之和 + 每级开销，钳 [45s 下限, 900s 上限]（与解析链 AiCallOrchestrator 同口径；
/// 对话 prompt 更大故下限 45s 略高于解析链）；单 provider 仍受协议内部 HttpClient.Timeout 约束。
/// 本地模型如 Ollama 首次加载慢，用户调大其 TimeoutSeconds 后，链路层不再用固定 45s 砍断（否则大超时白配）。
/// 错误一律「落审计 + 记日志 + 试下一个」；全失败抛 AiChatUnavailableException 给上层转 1000。
///
/// Audit_AiCall 落库（审计修复项 1）：测试集 Triage / SuggestRule 等对话类调用同为真实付费调用，
/// 与解析链（AiCallOrchestrator）同口径写审计——provider / model / 耗时 / token / 成败 / 错误类型 /
/// 升级链（同一次 CompleteAsync 共享 chainId，级序从 1 起），成功失败都落，监控页 7 天调用数 / 成功率 /
/// token 成本不再漏算，失败行同样进入 D3.4 健康统计的扫描窗口。表无「调用用途」专列（加列涉迁移，禁改），
/// 对话类行以 MediaItemId=null + Confidence=null 区分于解析链。链路超时同样补写 ErrorType=Timeout 行
/// （超时是全链预算耗尽，归因当级仅作诊断线索，不主动触发熔断评估）。
///
/// 健康熔断评估（D3.4）与解析链同口径：仅「接口故障」类失败（Transient/RateLimit/Http4xx/Http5xx/Logical）
/// 写完失败审计行后调 <see cref="IAiProviderHealthTracker.EvaluateAsync"/>，滚动窗口失败达阈值即自动禁用该
/// provider；ConfigError（配置漂移非 provider 故障）、Timeout（见上）与外部取消不触发——对话类失败不再只
/// 「落审计等被动扫描」，与解析链一样主动驱动熔断。
///
/// 套餐配额计量与解析链同口径：每处审计写入后并列调 <see cref="IAiProviderQuotaTracker.RecordUsageAsync"/>
/// （在 WriteAuditAsync 内收口，token 与审计行同值由构造保证），成功失败都计——失败请求也可能计费，保守保护钱包。
/// </remarks>
internal sealed class AiChatClient : IAiChatClient
{
    /// <summary>对话链路总超时分量：每级升级开销 + 下限 + 绝对上限护栏（口径同 AiCallOrchestrator）</summary>
    private static readonly TimeSpan ChainPerLevelOverhead = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChainMinTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ChainMaxTimeout = TimeSpan.FromSeconds(900);

    // 错误分类常量：与解析链（AiCallOrchestrator）写 Audit_AiCall.ErrorType 的口径保持一致（字符串需逐字相等）
    private const string ErrorTransient = "Transient";
    private const string ErrorRateLimit = "RateLimit";
    private const string ErrorLogical = "Logical";
    private const string ErrorHttp4xx = "Http4xx";
    private const string ErrorHttp5xx = "Http5xx";
    private const string ErrorConfigError = "ConfigError";
    private const string ErrorTimeout = "Timeout";

    private readonly IAiProviderResolver _resolver;
    private readonly IReadOnlyDictionary<AiProviderType, IAiProtocol> _protocols;
    private readonly IAuditAiCallWriter _audit;
    private readonly IAiProviderHealthTracker _healthTracker;
    private readonly IAiProviderQuotaTracker _quotaTracker;
    private readonly ILogger<AiChatClient> _logger;

    /// <summary>仅测试用：覆盖链路总超时（单测不必真实等待 45s 计时窗）</summary>
    internal TimeSpan? ChainTimeoutOverride { get; init; }

    public AiChatClient(
        IAiProviderResolver resolver,
        IEnumerable<IAiProtocol> protocols,
        IAuditAiCallWriter audit,
        IAiProviderHealthTracker healthTracker,
        IAiProviderQuotaTracker quotaTracker,
        ILogger<AiChatClient> logger)
    {
        _resolver = resolver;
        _protocols = protocols.ToDictionary(p => p.Protocol);
        _audit = audit;
        _healthTracker = healthTracker;
        _quotaTracker = quotaTracker;
        _logger = logger;
    }

    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        // 先用外部 ct 拿有序 provider（查 DB，很快），再按各级自身超时之和算链路总超时
        IReadOnlyList<AiProviderResolution> ordered = await _resolver.ResolveOrderedAsync(ct);
        if (ordered.Count == 0)
            throw new AiChatUnavailableException("未配置可用 AI 提供商");

        TimeSpan chainTimeout = ChainTimeoutOverride ?? ComputeChainTimeout(ordered);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(chainTimeout);
        CancellationToken linkedCt = linkedCts.Token;

        // 同一次 CompleteAsync 的多级尝试共享 chainId（与解析链口径一致，监控页可还原「主→备」轨迹）
        string chainId = Guid.NewGuid().ToString("N");
        List<string> attempts = new();
        int level = 0;
        foreach (AiProviderResolution res in ordered)
        {
            level++;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                AiCompletion completion = await CallProviderAsync(res, request, linkedCt);
                sw.Stop();
                string stripped = AiPromptHelpers.StripMarkdownFences(completion.Text).Trim();
                if (string.IsNullOrWhiteSpace(stripped))
                {
                    // HTTP 成功但内容为空：按 Logical 失败落审计后试下一个（与协议层「响应结构非法→Logical」同类）
                    await WriteAuditAsync(res, chainId, level, success: false, sw,
                        ErrorLogical, "返回空内容",
                        completion.PromptTokens, completion.CompletionTokens,
                        httpStatus: 200, request.UserPrompt, completion.Text, ct);
                    // Logical 属「接口故障」口径：与解析链一致，失败审计行落库后主动触发健康熔断评估（D3.4）
                    await _healthTracker.EvaluateAsync(res.ProviderId, ct);
                    attempts.Add($"{res.Name}: 返回空内容");
                    continue;
                }
                // 成功：落审计（token / 模型 / 耗时进监控页成本与成功率统计）
                await WriteAuditAsync(res, chainId, level, success: true, sw,
                    errorType: null, errorDetail: null,
                    completion.PromptTokens, completion.CompletionTokens,
                    httpStatus: 200, request.UserPrompt, completion.Text, ct);
                return new AiChatResult(stripped, res.ProviderId, res.Name);
            }
            catch (OperationCanceledException) when (linkedCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // 链路总超时（非外部取消）：给正在等待的当级 provider 补写 Timeout 审计行（用原始 ct，
                // linkedCt 已取消写不进库），保证「哪家 provider 挂死」可诊断（审计修复项 2 同款缺口）
                sw.Stop();
                await WriteAuditAsync(res, chainId, level, success: false, sw,
                    ErrorTimeout,
                    $"AI 对话链路总超时（{chainTimeout.TotalSeconds:F0} 秒），第 {level} 级 provider「{res.Name}」已等待 {sw.Elapsed.TotalSeconds:F1} 秒仍未返回",
                    promptTokens: null, completionTokens: null,
                    httpStatus: null, request.UserPrompt, responseText: null, ct);
                throw new AiChatUnavailableException($"AI 对话链路超时（{chainTimeout.TotalSeconds:F0} 秒），当前提供商：{res.Name}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 失败也落审计：错误分类与解析链同口径；HTTP 状态 / token / 响应原文经 Exception.Data 透传（AiCallDiagnostics）
                sw.Stop();
                string errorType = ClassifyError(ex);
                await WriteAuditAsync(res, chainId, level, success: false, sw,
                    errorType, ex.Message,
                    ex.Data[AiCallDiagnostics.PromptTokensKey] as int?,
                    ex.Data[AiCallDiagnostics.CompletionTokensKey] as int?,
                    (ex.Data[AiCallDiagnostics.HttpStatusKey] as int?) ?? (ex as AiProviderLogicalException)?.HttpStatus,
                    request.UserPrompt,
                    ex.Data[AiCallDiagnostics.ResponseTextKey] as string, ct);
                // 仅「接口故障」触发健康熔断评估（D3.4），与解析链同口径；ConfigError 是配置漂移非 provider 故障，不触发
                if (IsProviderFault(errorType))
                    await _healthTracker.EvaluateAsync(res.ProviderId, ct);
                _logger.LogWarning(ex, "AI 对话 provider {Provider} 失败，尝试下一个", res.Name);
                attempts.Add($"{res.Name}: {ex.Message}");
            }
        }

        throw new AiChatUnavailableException($"全部 AI 提供商均失败：{string.Join(" | ", attempts)}");
    }

    /// <summary>按 res.Type 路由到协议策略，组 system/user 两条消息后委托协议补全，返回原始补全（含 token 用量）</summary>
    private async Task<AiCompletion> CallProviderAsync(AiProviderResolution res, AiChatRequest request, CancellationToken ct)
    {
        if (!_protocols.TryGetValue(res.Type, out IAiProtocol? protocol))
            throw new InvalidOperationException($"无 IAiProtocol 实现：{res.Type}");

        List<AiChatMessage> messages =
        [
            new("system", request.SystemPrompt),
            new("user", request.UserPrompt),
        ];

        return await protocol.CompleteAsync(
            new AiProtocolRequest(res.Endpoint, messages, JsonMode: request.JsonMode, Temperature: request.Temperature),
            ct);
    }

    /// <summary>对话调用落 Audit_AiCall + 套餐配额计量（与解析链同字段口径；MediaItemId=null 标识非文件解析类调用）</summary>
    /// <remarks>
    /// RequestText 仅存 user 提示词（不含恒定 system 提示，省体积，同解析链约定）；写入端按安全阀截断。
    /// 配额计量在审计写入后并列执行（成功失败都计，token 与审计行同值由本方法收口保证）。
    /// </remarks>
    private async Task WriteAuditAsync(
        AiProviderResolution res, string chainId, int level, bool success, Stopwatch sw,
        string? errorType, string? errorDetail, int? promptTokens, int? completionTokens,
        int? httpStatus, string requestText, string? responseText, CancellationToken ct)
    {
        await _audit.WriteAsync(new AuditAiCallEntry(
            res.ProviderId, MediaItemId: null, success, (int)sw.Elapsed.TotalMilliseconds,
            errorType, errorDetail,
            Model: res.Endpoint.Model,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Confidence: null,
            HttpStatus: httpStatus,
            ChainId: chainId,
            AttemptLevel: level,
            IsPrimary: res.IsPrimary,
            RequestText: requestText,
            ResponseText: responseText), ct);
        await _quotaTracker.RecordUsageAsync(res.ProviderId, promptTokens, completionTokens, ct);
    }

    /// <summary>是否「接口故障」类错误（与解析链同口径：仅此类失败触发 D3.4 健康熔断评估）</summary>
    /// <remarks>排除项：ConfigError（配置漂移非 provider 故障）、Timeout（全链预算耗尽，归因当级仅作诊断线索）。</remarks>
    private static bool IsProviderFault(string errorType) =>
        errorType is ErrorTransient or ErrorRateLimit or ErrorHttp4xx or ErrorHttp5xx or ErrorLogical;

    /// <summary>链路总超时 = 各级 provider 自身超时之和 + 每级开销，钳 [下限, 上限]（口径同 AiCallOrchestrator）</summary>
    private static TimeSpan ComputeChainTimeout(IReadOnlyList<AiProviderResolution> ordered)
    {
        double seconds = 0;
        foreach (AiProviderResolution res in ordered)
            seconds += Math.Max(1, res.Endpoint.TimeoutSeconds) + ChainPerLevelOverhead.TotalSeconds;
        TimeSpan timeout = TimeSpan.FromSeconds(seconds);
        if (timeout < ChainMinTimeout) timeout = ChainMinTimeout;
        if (timeout > ChainMaxTimeout) timeout = ChainMaxTimeout;
        return timeout;
    }

    /// <summary>异常 → Audit ErrorType 分类（与 AiCallOrchestrator 的升级原因口径一致）</summary>
    private static string ClassifyError(Exception ex) => ex switch
    {
        AiProviderTransientException => ErrorTransient,
        AiProviderRateLimitException => ErrorRateLimit,
        AiProviderLogicalException { HttpStatus: >= 400 and < 500 } => ErrorHttp4xx,
        AiProviderLogicalException { HttpStatus: >= 500 } => ErrorHttp5xx,
        AiProviderLogicalException => ErrorLogical,
        InvalidOperationException => ErrorConfigError,   // 无 IAiProtocol 实现（配置漂移）
        _ => ErrorLogical,
    };
}
