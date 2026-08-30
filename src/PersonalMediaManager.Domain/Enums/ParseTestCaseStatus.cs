namespace PersonalMediaManager.Domain.Enums;

/// <summary>解析测试用例状态（Parse_TestCase.Status）</summary>
/// <remarks>
/// 状态机：
///   - PendingTriage：来自失败历史灌入暂存区，等待用户手动或 AI 判定是否纳入
///   - Active：正式样本，纳入回归集合；每次规则变动后批量重跑
///   - Disabled：暂时停用（保留数据，但不参与批量回归与统计）
/// EF 配置为字符串列（HasConversion&lt;string&gt;().HasMaxLength(16)），便于库内人读。
/// </remarks>
public enum ParseTestCaseStatus
{
    /// <summary>暂存区：等待判定是否纳入</summary>
    PendingTriage = 1,

    /// <summary>正式样本：参与回归</summary>
    Active = 2,

    /// <summary>停用：保留数据但不参与回归</summary>
    Disabled = 3,
}
