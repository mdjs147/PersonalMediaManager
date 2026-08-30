using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>AI 提供商解析（D3.2）— 按 IsPrimary + Priority 输出可用 provider 的升级序列</summary>
/// <remarks>
/// 调用顺序约束（需求文档 §3.3.3 + 多级升级扩展）：
///   1) IsPrimary=true 且当前可用 → 必排第 1 级（全表至多 1 行；通常配本地 Ollama）
///   2) 其余 Enabled=true 且未被下述条件剔除 → 按 Priority 升序排在主之后，构成「逐级升级到更高级 AI」的阶梯
///   3) 剔除条件（任一命中即不可用）：Enabled=false（用户开关）/ DisabledUntil &gt; now（健康熔断，到点自动恢复）
///      / QuotaExceededAt 非空（套餐用量超限，放宽限额或 reset-quota 才解除）/ QuotaExpiresAt &lt;= now（套餐到期，纯时间过滤）
/// 返回的 AiProviderResolution 已包含解密后的 AiProviderEndpoint + 解析好的满意度阈值（ConfidenceThreshold），
/// 调用方（AiCallOrchestrator D3.3）经 IAiParser 门面按 Type 路由到协议解析，并按阈值做质量门。
/// 实现位于 Infrastructure.Persistence/Services/Parse/AiProviderResolver.cs：每次按需查 DB，
/// 不缓存（自动禁用窗口随时变化；CRUD 写入也立即生效）；per-provider 阈值为空时回退全局 Parse.AiConfidenceThreshold。
/// </remarks>
public interface IAiProviderResolver
{
    /// <summary>返回当前可用 provider 的有序升级序列；若全为空返回空列表（调用方自行判断走人工）</summary>
    Task<IReadOnlyList<AiProviderResolution>> ResolveOrderedAsync(CancellationToken ct = default);
}

/// <summary>已解密的 provider 调用配置（不含协议实现实例，避免 Application 引 External 实现）</summary>
/// <param name="ProviderId">DB 主键（Audit_AiCall.ProviderId 与 DisabledUntil 写入使用）</param>
/// <param name="Type">类型（Orchestrator 经 IAiParser 用 Type 路由到具体协议实现）</param>
/// <param name="Name">展示名（日志用）</param>
/// <param name="IsPrimary">是否主提供商（升级序列第 1 级）</param>
/// <param name="Endpoint">已解密的端点（BaseUrl/ApiKey/Model/TimeoutSeconds）</param>
/// <param name="ConfidenceThreshold">满意度阈值（0~1）：本级返回置信度低于此值 → 升级到下一级；已由 Resolver 回退好全局默认</param>
/// <param name="RpmLimit">每分钟请求上限（RPM，滑动 60 秒窗口）：Orchestrator 调用前检查，本窗口达上限则「跳过」本级直接升级下一级；null=不限流</param>
public sealed record AiProviderResolution(
    long ProviderId,
    AiProviderType Type,
    string Name,
    bool IsPrimary,
    AiProviderEndpoint Endpoint,
    double ConfidenceThreshold = 0.7,
    int? RpmLimit = null);
