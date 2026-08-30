namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>AI 提供商健康追踪（D3.4）— 滚动窗口失败次数 ≥ 阈值 → 写 DisabledUntil 自动禁用</summary>
/// <remarks>
/// 调用时机：AiCallOrchestrator 每次写完 Audit_AiCall 失败行后调一次 EvaluateAsync；
/// 实现统计 (ProviderId, Success=false) 在 [now - WindowMinutes, now] 内的行数：
///   - ≥ FailureThreshold（默认 5） → 写 Parse_AiProvider.DisabledUntil = now + CooldownMinutes（默认 30）
///   - 否则不动
/// 已禁用（DisabledUntil &gt; now）则不重置（避免「窗口内连续失败把禁用延后」的悖论）。
/// 手动 /enable 端点（C.5）会清 DisabledUntil，与本追踪器无冲突（清空后下次窗口扫描重新累计）。
/// 配置项目前硬编码（10min / 5 / 30min），后续 D7 阶段如需走 System_Setting 表，直接读 IClock + 配置注入。
/// </remarks>
public interface IAiProviderHealthTracker
{
    Task EvaluateAsync(long providerId, CancellationToken ct = default);
}
