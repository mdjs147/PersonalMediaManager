using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common.Diagnostics;
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
internal sealed class AiProtocolParser : IAiParser
{
    private readonly IReadOnlyDictionary<AiProviderType, IAiProtocol> _protocols;

    public AiProtocolParser(IEnumerable<IAiProtocol> protocols)
    {
        _protocols = protocols.ToDictionary(p => p.Protocol);
    }

    public bool Supports(AiProviderType protocol) => _protocols.ContainsKey(protocol);

    public async Task<AiParseOutcome> ParseAsync(
        AiProviderType protocol,
        AiProviderEndpoint endpoint,
        AiParseRequest request,
        CancellationToken ct = default)
    {
        if (!_protocols.TryGetValue(protocol, out IAiProtocol? impl))
            throw new AiProviderLogicalException($"无 IAiProtocol 实现：{protocol}");

        // 诊断同时捕获实际发送的系统与用户提示词；旧审计契约继续只返回用户提示词。
        AiPromptHelpers.PreparedTaskPrompt? prepared = request.Context is null ? null : AiPromptHelpers.PrepareTaskPrompt(request);
        string userPrompt = prepared?.UserPrompt ?? AiPromptHelpers.BuildUserPrompt(request);
        List<AiChatMessage> messages =
        [
            new("system", prepared is null ? AiPromptHelpers.SystemPrompt : AiPromptHelpers.GetTaskSystemPrompt(prepared.Request)),
            new("user", userPrompt),
        ];

        int maxTokens = request.Context is { SchemaVersion: 2, OutputDetail: AiOutputDetail.Compact } ? 512 : 1024;
        AiProtocolRequest protocolRequest = new(endpoint, messages, JsonMode: endpoint.StructuredJson,
            Temperature: 0, MaxTokens: maxTokens);
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
            endpoint.TimeoutSeconds, Stream = false, Fingerprint = fingerprint,
            System = systemText, User = userText,
            Metadata = prepared?.Metadata,
        });

        AiCompletion completion;
        try
        {
            completion = await impl.CompleteAsync(protocolRequest, ct);
            ParseDiagnostics.Emit("ai.response", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds,
                completion.PromptTokens, completion.CompletionTokens, Response = Capture(completion.Text),
            });
        }
        catch (Exception ex)
        {
            // 不序列化异常对象或端点，避免凭据、请求头及私有推理进入诊断。
            ex.Data[AiCallDiagnostics.RequestTextKey] = RemoveCredential(userPrompt);
            if (ex.Data[AiCallDiagnostics.ResponseTextKey] is string body)
                ex.Data[AiCallDiagnostics.ResponseTextKey] = RemoveCredential(body);
            ParseDiagnostics.Emit(ex is OperationCanceledException ? "ai.cancelled" : "ai.failed", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds,
                ExceptionType = ex.GetType().Name, CallerCancelled = ct.IsCancellationRequested,
                HttpStatus = ex.Data[AiCallDiagnostics.HttpStatusKey] as int?,
                Error = Capture(ex.Message), Response = ex.Data.Contains(AiCallDiagnostics.ResponseTextKey)
                    ? Capture(ex.Data[AiCallDiagnostics.ResponseTextKey] as string) : ParseDiagnostics.UnknownText(),
            });
            throw;
        }

        try
        {
            AiParseResult result = prepared is null ? AiPromptHelpers.ParseContent(completion.Text)
                : AiPromptHelpers.ParseTaskContent(completion.Text, prepared.Request);
            // 字面溯源守护：剔除 AI 凭作品名幻觉、文件名 / 路径里根本没写的年份（多版本作品会锚到最早版本，污染 TMDB 匹配）
            if (prepared is null) result = AiPromptHelpers.GroundYear(result, request);
            ParseDiagnostics.Emit("ai.parse_result", new
            {
                Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds, Parsed = true,
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
            ParseDiagnostics.CaptureText(RemoveCredential(text), includeAtStandard);
    }
}
