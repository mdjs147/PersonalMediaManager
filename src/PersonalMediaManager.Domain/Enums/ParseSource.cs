namespace PersonalMediaManager.Domain.Enums;

/// <summary>解析结果的来源（最终采纳的）</summary>
/// <remarks>
/// 写入 Media_Item.ParseSource 字段，便于统计规则命中率（仪表盘指标，需求文档 §3.10）。
/// Hybrid：规则解析 + AI 兜底联合决策（如规则命中标题但 AI 修正年份）。
/// Manual：命中 pmm.txt 强制匹配标识 / TMDB URL，直接锚定指定条目，跳过规则与 AI 的自主判断。
/// </remarks>
public enum ParseSource
{
    /// <summary>规则引擎命中</summary>
    Rule = 1,

    /// <summary>AI 兜底</summary>
    Ai = 2,

    /// <summary>规则 + AI 联合（混合修正）</summary>
    Hybrid = 3,

    /// <summary>强制匹配（pmm.txt 标识文件 / TMDB URL 锚定，跳过规则与 AI 自主判断）</summary>
    Manual = 4,
}
