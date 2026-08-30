namespace PersonalMediaManager.Application.Services.Audit;

/// <summary>Audit_AiCall 写入器（D3.3 + D3.4）</summary>
/// <remarks>
/// 每次 AiCallOrchestrator 完成一次 provider 调用（无论成功失败）写一条；
/// AiProviderHealthTracker（D3.4）按 (ProviderId, Success, Timestamp) 复合索引扫窗口判定自动禁用。
/// ErrorType 取值：Timeout / Http4xx / Http5xx / Parse / Network / Transient（与 §需求文档 §3.3.3 对齐）。
/// </remarks>
public interface IAuditAiCallWriter
{
    /// <summary>写一行 AI 调用审计（入参聚成 <see cref="AuditAiCallEntry"/>）</summary>
    Task WriteAsync(AuditAiCallEntry entry, CancellationToken ct = default);
}
