namespace PersonalMediaManager.Domain.Enums;

/// <summary>AI 节点成本档位（Parse_AiProvider.CostTier）</summary>
/// <remarks>
/// <b>独立于协议（<see cref="AiProviderType"/>）的正交维度</b>：同一协议既可能是付费节点也可能是免费节点。
/// 档位影响运行时策略而非 wire 格式：
///   - Free（免费档）→ 享<b>节流豁免</b>（不下发 1 req/s 限速，免费节点无配额压力）+ <b>高满意度阈值默认</b>（逼它把握不足就升级到付费节点）。
///   - Paid（付费档）→ 走进程级 1 req/s 节流，使用常规阈值兜底。
/// 数据库以字符串存储（"Paid" / "Free"），EF 配置 HasConversion。
/// </remarks>
public enum AiCostTier
{
    /// <summary>付费档：走节流，常规阈值</summary>
    Paid = 1,

    /// <summary>免费档：节流豁免，高阈值默认</summary>
    Free = 2,
}
