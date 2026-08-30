namespace PersonalMediaManager.Domain.Enums;

/// <summary>解析规则作用域（Parse_Rule.Scope）</summary>
/// <remarks>
/// EF 配置为字符串列（ParseRuleConfig：HasConversion&lt;string&gt;().HasMaxLength(16)），
/// 新增值后既存数据库行向后兼容（既存值仍能反序列化）。
///
/// FileName / ParentFolder / FullPath 三个旧值保留兼容；新调用方建议使用 AllAncestors / RelativePath：
///   - FileName     : 仅文件名（不含后缀）
///   - ParentFolder : 直接父目录段（FileParseContext.DirectParentFolderName）
///   - FullPath     : 兼容旧值 = 直接父目录 + 文件名拼接（语义上等于 RelativePath 的「只看末两段」）
///   - AllAncestors : 内层→外层逐段尝试，规则按层级补全季/集/标题，置信度加成随层级递减
///   - RelativePath : 监控根→文件的整条相对路径（用 / 标准化分隔符）一次性匹配
/// </remarks>
public enum ParseScope
{
    /// <summary>仅文件名（不含目录与后缀）</summary>
    FileName = 1,

    /// <summary>直接父目录名</summary>
    ParentFolder = 2,

    /// <summary>完整路径（兼容旧值，实际等于直接父目录 + 文件名）</summary>
    FullPath = 3,

    /// <summary>所有祖先目录段（内层→外层逐段尝试，按层级补全字段）</summary>
    AllAncestors = 4,

    /// <summary>监控根→文件的整条相对路径（标准化为 / 分隔）</summary>
    RelativePath = 5,
}
