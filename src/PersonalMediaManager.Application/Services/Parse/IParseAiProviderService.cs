using PersonalMediaManager.Application.Dtos.Parse;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>AI 提供商服务契约（Parse_AiProvider CRUD + /test + /enable + /reset-quota）</summary>
/// <remarks>
/// ApiKey 走 IProtectedFieldService 加密；响应仅暴露 HasApiKey 布尔。
/// IsPrimary=true 全表唯一：保存时若新主，旧主自动降级。
/// /test：从 DB 读出后解密 ApiKey 走 IAiProviderTester 探测，结果不写库（D 阶段健康追踪才写）。
/// /enable：手动解禁清 DisabledUntil（不动 Enabled 标志，也不绕过配额禁用 QuotaExceededAt）。
/// /reset-quota：清零 QuotaUsedCalls/QuotaUsedTokens + 清 QuotaExceededAt（新套餐周期/续购）。
/// Update 保存前统一重评估 QuotaExceededAt：限额放宽（调高/清除）且不再超限自动清，收紧至超限则置。
/// </remarks>
public interface IParseAiProviderService
{
    Task<IReadOnlyList<ParseAiProviderResponse>> ListAsync(CancellationToken ct = default);

    Task<ParseAiProviderResponse> CreateAsync(CreateParseAiProviderRequest req, CancellationToken ct = default);

    Task<ParseAiProviderResponse> UpdateAsync(UpdateParseAiProviderRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteParseAiProviderRequest req, CancellationToken ct = default);

    Task<TestParseAiProviderResponse> TestAsync(TestParseAiProviderRequest req, CancellationToken ct = default);

    Task EnableAsync(EnableParseAiProviderRequest req, CancellationToken ct = default);

    /// <summary>重置套餐用量：清零两个 Used 计数器并解除配额禁用（开始新套餐周期/续购时用）</summary>
    Task ResetQuotaAsync(ResetQuotaParseAiProviderRequest req, CancellationToken ct = default);

    /// <summary>聚合 Audit_AiCall 给指定 provider 在 [now - windowHours, now] 区间的调用统计</summary>
    Task<AiProviderStatsResponse> GetStatsAsync(long providerId, int windowHours, CancellationToken ct = default);

    /// <summary>分页查询指定 provider 的 Audit_AiCall 调用日志（支持成功/错误类型/链/时间过滤，含原文）</summary>
    Task<AiCallLogPageResponse> GetLogsAsync(long providerId, AiCallLogQuery query, CancellationToken ct = default);
}
