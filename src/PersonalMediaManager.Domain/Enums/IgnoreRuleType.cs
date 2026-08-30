namespace PersonalMediaManager.Domain.Enums;

/// <summary>忽略规则类型（Watch_IgnoreRule.Type）</summary>
public enum IgnoreRuleType
{
    /// <summary>按扩展名（含点，如 .part）</summary>
    Extension = 1,

    /// <summary>按关键词（子串匹配，小写归一化）</summary>
    Keyword = 2,
}
