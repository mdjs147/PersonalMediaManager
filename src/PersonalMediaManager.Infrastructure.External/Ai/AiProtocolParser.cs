using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>IAiParser 实现：按协议路由到 IAiProtocol + 复用 AiPromptHelpers 拼提示词/反解</summary>
/// <remarks>
/// 把解析专用的 system / user 提示词（<see cref="AiPromptHelpers.SystemPrompt"/> / <see cref="AiPromptHelpers.BuildUserPrompt"/>）
/// 与 JSON 反解（<see cref="AiPromptHelpers.ParseContent"/>）封装在 External 层，使编排层只依赖 <see cref="IAiParser"/> 抽象，
/// 不反向耦合 Infrastructure.External 的工具类。
/// JsonMode 取端点能力 <see cref="AiProviderEndpoint.StructuredJson"/>；温度恒 0（解析要确定性输出）。
/// 多个相同 Protocol 的 <see cref="IAiProtocol"/> 不允许（DI 每协议一个）；ToDictionary 自然报重复。
/// </remarks>
internal sealed partial class AiProtocolParser : IAiParser, IAiPhysicalRequestParser
{
    private readonly IReadOnlyDictionary<AiProviderType, IAiProtocol> _protocols;

    private readonly AiBatchOptions _batch;
    private readonly IAiBatchSettingsService? _batchSettings;

    public AiProtocolParser(IEnumerable<IAiProtocol> protocols, AiBatchOptions? batch = null, IAiBatchSettingsService? batchSettings = null)
    {
        _protocols = protocols.ToDictionary(p => p.Protocol);
        _batch = batch ?? new();
        _batch.Validate();
        _batchSettings = batchSettings;
    }

    public bool Supports(AiProviderType protocol) => _protocols.ContainsKey(protocol);

    public async Task<AiParseOutcome> ParseAsync(
        AiProviderType protocol,
        AiProviderEndpoint endpoint,
        AiParseRequest request,
        CancellationToken ct = default)
    {
        AiTransportScope? transport = AiTransportScope.Current;
        AiBatchOptions options = _batchSettings is null ? _batch : (await _batchSettings.GetAsync(ct)).ToOptions(
            transport?.ProviderId, AiBatchProviderPresets.ConfigurationKey(protocol, endpoint.BaseUrl, endpoint.Model));
        int inputBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request));
        if (inputBytes > AiBatchOptions.MaxItemInputBytes)
        {
            ParseDiagnostics.Emit("ai.batch_budget_rejected", new { Reason = "item_input_bytes", InputBytes = inputBytes,
                Limit = AiBatchOptions.MaxItemInputBytes });
            throw new AiProviderLogicalException("AI 单项输入超出 64 KiB 安全上限");
        }
        if (options.ExternalMaxItems == 1) return await ParseSingleAsync(protocol, endpoint, request, transport, ct,
            budget: options.ContextTokenBudget == 8192 && options.MaxOutputTokens == 2048 && options.MaxResponseBytes == 65536 && !options.DisableThinking ? null : options);
        BatchInput input = new(protocol, endpoint, request, transport, ParseDiagnostics.CaptureEmitter(), ParseDiagnostics.CaptureActivation(), options);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(endpoint.TimeoutSeconds, 1, 600)));
        Task<BatchResult>? scheduled = AiBatchPipeline.Schedule(CompatibilityKey(input), input, ExecuteBatchAsync, timeout.Token,
            () => { timeout.CancelAfter(Timeout.InfiniteTimeSpan); transport?.ResponseReady(); }, inputBytes, options.ExternalMaxItems);
        if (scheduled is not null)
        {
            BatchResult result;
            try { result = await scheduled; }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            { throw new AiProviderLogicalException("AI 批处理含等待时间的总请求预算已超时", inner: new TaskCanceledException("AI 请求超时", ex)); }
            finally { transport?.ResponseReleased(); }
            if (result.Error is AiProviderBatchDeferredException)
                (transport?.CallerCancellationToken ?? ct).ThrowIfCancellationRequested();
            if (result.Error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error).Throw();
            return result.Outcome!;
        }
        return await ParseSingleAsync(protocol, endpoint, request, transport, ct, budget: options);
    }

    private async Task<AiParseOutcome> ParseSingleAsync(AiProviderType protocol, AiProviderEndpoint endpoint,
        AiParseRequest request, AiTransportScope? transport, CancellationToken ct, Action? onSending = null, string? batchId = null, string? itemId = null, AiBatchOptions? budget = null)
    {
        if (!_protocols.TryGetValue(protocol, out IAiProtocol? impl))
            throw new AiProviderLogicalException($"无 IAiProtocol 实现：{protocol}");

        using IDisposable physicalDiagnostic = ParseDiagnostics.BeginAiCall(Guid.NewGuid().ToString("N"), batchId, itemId, credential: endpoint.ApiKey);
        if (ParseDiagnostics.IsFull) ParseDiagnostics.Emit("ai.input", new { stage = "original_request_context", content = Capture(JsonSerializer.Serialize(request)) });
        // 诊断同时捕获准备发送的系统与用户提示词；实际调用以 HTTP dispatch 事件为准。
        AiPromptHelpers.PreparedTaskPrompt? prepared = request.Context is null ? null : AiPromptHelpers.PrepareTaskPrompt(request);
        string userPrompt = prepared?.UserPrompt ?? AiPromptHelpers.BuildUserPrompt(request);
        List<AiChatMessage> messages =
        [
            new("system", prepared is null ? AiPromptHelpers.SystemPrompt : AiPromptHelpers.GetTaskSystemPrompt(prepared.Request)),
            new("user", userPrompt),
        ];

        int maxTokens = request.Context is { SchemaVersion: 2, OutputDetail: AiOutputDetail.Compact } ? 512 : 1024;
        if (budget is not null && (maxTokens > budget.MaxOutputTokens || AiBatchJson.TokenUpperBound(messages[0].Content)
            + AiBatchJson.TokenUpperBound(userPrompt) + maxTokens > budget.ContextTokenBudget))
        {
            ParseDiagnostics.Emit("ai.batch_budget_rejected", new { Reason = "single_does_not_fit", maxTokens,
                budget.MaxOutputTokens, budget.ContextTokenBudget });
            throw new AiProviderLogicalException("AI 单项请求无法容纳于当前上下文或输出预算，请核对提供商批量预算");
        }
        AiProtocolRequest protocolRequest = new(endpoint, messages, JsonMode: endpoint.StructuredJson,
            Temperature: 0, MaxTokens: maxTokens, MaxResponseBytes: budget?.MaxResponseBytes ?? 262144, DisableThinking: budget?.DisableThinking ?? false);
        using IDisposable? diagnosticScope = ParseDiagnostics.CurrentRunId is null ? ParseDiagnostics.Begin("ai_protocol") : null;
        DiagnosticText systemText = Capture(messages[0].Content);
        DiagnosticText userText = Capture(userPrompt);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Protocol = protocol.ToString(), ModelSha256 = Capture(endpoint.Model).Sha256, protocolRequest.JsonMode,
            protocolRequest.Temperature, protocolRequest.MaxTokens,
            SystemSha256 = systemText.Sha256, UserSha256 = userText.Sha256,
        }))));
        Stopwatch elapsed = Stopwatch.StartNew();
        ParseDiagnostics.Emit("ai.request", new
        {
            Protocol = protocol.ToString(), Model = Capture(endpoint.Model, includeAtStandard: true),
            protocolRequest.JsonMode, protocolRequest.Temperature, protocolRequest.MaxTokens,
            endpoint.TimeoutSeconds, Stream = false, Fingerprint = fingerprint, Stage = "prepared_not_yet_sent",
            System = systemText, User = userText,
            Metadata = prepared?.Metadata,
        });

        AiCompletion completion;
        int? usageIndex = null;
        async Task BeforeSend(CancellationToken sendToken)
        {
            usageIndex = transport is null ? null : await transport.StartedAsync(sendToken);
            onSending?.Invoke();
        }
        protocolRequest = protocolRequest with { BeforeSend = BeforeSend };
        try
        {
            ct.ThrowIfCancellationRequested();
            if (impl is not IAiSendBoundaryProtocol) await BeforeSend(ct);
            completion = await impl.CompleteAsync(protocolRequest, ct);
            if (usageIndex is int index) await transport!.CompletedAsync(index, completion.PromptTokens, completion.CompletionTokens);
            ParseDiagnostics.Emit("ai.response", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds,
                completion.PromptTokens, completion.CompletionTokens, Response = Capture(completion.Text) with { Boundary = "redacted_extracted_assistant_content" },
            });
        }
        catch (Exception ex)
        {
            // 不序列化异常对象或端点，避免凭据、请求头及私有推理进入诊断。
            string? observedFailureBody = ex.Data[AiCallDiagnostics.ResponseTextKey] as string;
            ex.Data[AiCallDiagnostics.RequestTextKey] = RemoveCredential(userPrompt);
            if (ex.Data[AiCallDiagnostics.ResponseTextKey] is string body)
                ex.Data[AiCallDiagnostics.ResponseTextKey] = RemoveCredential(body);
            ParseDiagnostics.Emit(ex is OperationCanceledException ? "ai.cancelled" : "ai.failed", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds,
                ExceptionType = ex.GetType().Name, CallerCancelled = ct.IsCancellationRequested,
                HttpStatus = ex.Data[AiCallDiagnostics.HttpStatusKey] as int?,
                Error = Capture(ex.Message), Response = ex.Data.Contains(AiCallDiagnostics.ResponseTextKey)
                    ? Capture(observedFailureBody) : ParseDiagnostics.UnknownText(),
            });
            throw;
        }

        finally
        {
            if (usageIndex is int index) await transport!.SettleAsync(index);
        }

        try
        {
            if (completion.IsTruncated) throw new AiProviderLogicalException("AI 输出达到长度限制，拒绝采用截断结果");
            AiParseResult result = prepared is null ? AiPromptHelpers.ParseContent(completion.Text)
                : AiPromptHelpers.ParseTaskContent(completion.Text, prepared.Request);
            // 字面溯源守护：剔除 AI 凭作品名幻觉、文件名 / 路径里根本没写的年份（多版本作品会锚到最早版本，污染 TMDB 匹配）
            if (prepared is null) result = AiPromptHelpers.GroundYear(result, request);
            ParseDiagnostics.Emit("ai.parse_result", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds, Parsed = true,
                Structured = ParseDiagnostics.IsFull ? Capture(JsonSerializer.Serialize(result)) with { Boundary = "serialized_final_structured_result" } : null,
                Title = Capture(result.Title), result.Year, result.MediaType, result.Season,
                result.Episode, result.EpisodeEnd,
                Confidence = double.IsFinite(result.Confidence) ? (double?)result.Confidence : null,
                ConfidenceState = double.IsFinite(result.Confidence) ? "recorded" : "invalid",
                result.SelectedCandidateId,
                result.Abstained, result.RequiresIdentityVerification,
                Validation = ParseDiagnostics.CaptureText(JsonSerializer.Serialize(result.Validation), includeAtStandard: true),
            });
            return new AiParseOutcome(result, RemoveCredential(userPrompt), RemoveCredential(completion.Text), completion.PromptTokens, completion.CompletionTokens, prepared?.Metadata);
        }
        catch (AiProviderLogicalException ex)
        {
            // JSON 反解失败：补挂请求 + 响应原文 + token，便于诊断「AI 究竟返回了什么」
            ex.Data[AiCallDiagnostics.RequestTextKey] = RemoveCredential(userPrompt);
            ex.Data[AiCallDiagnostics.ResponseTextKey] = RemoveCredential(completion.Text);
            if (completion.PromptTokens is int pt) ex.Data[AiCallDiagnostics.PromptTokensKey] = pt;
            if (completion.CompletionTokens is int ctk) ex.Data[AiCallDiagnostics.CompletionTokensKey] = ctk;
            ParseDiagnostics.Emit("ai.parse_result", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds, Parsed = false,
                ExceptionType = ex.GetType().Name, Error = Capture(ex.Message),
            });
            throw;
        }

        string? RemoveCredential(string? text) => string.IsNullOrEmpty(endpoint.ApiKey) ? text
            : text?.Replace(endpoint.ApiKey, "[凭据已脱敏]", StringComparison.Ordinal);
        DiagnosticText Capture(string? text, bool includeAtStandard = false) =>
            ParseDiagnostics.CaptureText(text, includeAtStandard, credential: endpoint.ApiKey);
    }
}
