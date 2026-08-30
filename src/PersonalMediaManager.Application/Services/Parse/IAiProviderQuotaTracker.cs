namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>AI 提供商套餐配额计量 — 调用后计次/计 token，超限幂等置 QuotaExceededAt 自动禁用</summary>
/// <remarks>
/// 调用时机：AiCallOrchestrator（解析链）/ AiChatClient（对话链）每次写完 Audit_AiCall 行后调一次，
/// 成功失败都计（失败请求也可能计费，保守保护钱包）；AiProviderTester（测试连接）不落审计也不计量。
/// 实现累计 Parse_AiProvider.QuotaUsedCalls / QuotaUsedTokens 独立计数器
/// （不依赖 Audit_AiCall 聚合——该表有保留期清理会截断历史），累计后评估：
/// 配置了 QuotaCallLimit / QuotaTokenLimit 且用量达限 → 幂等置 QuotaExceededAt = now（仅原值 null 时），
/// 置位成功才发中文警告日志 + ai.provider_quota_exceeded 告警（经 IAlertService，抑制窗口内不重复）。
/// 配额禁用与 Enabled（用户开关）、DisabledUntil（健康熔断）三态分离，Resolver 侧独立过滤；
/// 解除路径：Update 放宽限额自动清 / reset-quota 端点清零计数（新套餐周期）。
/// </remarks>
public interface IAiProviderQuotaTracker
{
    /// <summary>记录一次实际 AI HTTP 调用：次数 +1、token += (prompt??0)+(completion??0)，累计后评估超限</summary>
    Task RecordUsageAsync(long providerId, int? promptTokens, int? completionTokens, CancellationToken ct = default);
}
