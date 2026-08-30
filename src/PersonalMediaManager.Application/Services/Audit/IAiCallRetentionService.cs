namespace PersonalMediaManager.Application.Services.Audit;

/// <summary>Audit_AiCall 留存清理 — 按天龄 + 单 provider 行数上限双闸裁剪</summary>
/// <remarks>
/// 被 AiCallRetentionJob 每日触发。两道闸读 System_Setting（可调）：
///   · Audit_AiCallRetentionDays（默认 90）：删 Timestamp 早于 now-Days 的行；≤0 跳过该闸
///   · Audit_AiCallMaxRowsPerProvider（默认 50000）：每 provider 仅保留最新 N 行，超额删最旧；≤0 跳过该闸
/// 监控增强后每行可能带请求/响应原文，留存清理防其累积撑爆 SQLite；家用规模 90 天足够回溯。返回删除总行数。
/// </remarks>
public interface IAiCallRetentionService
{
    /// <summary>执行一次双闸清理，返回删除的行数</summary>
    Task<int> PurgeAsync(CancellationToken ct = default);
}
