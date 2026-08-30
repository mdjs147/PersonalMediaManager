namespace PersonalMediaManager.Domain.Enums;

/// <summary>解析测试用例来源（Parse_TestCase.Source）</summary>
/// <remarks>
/// EF 配置为字符串列（HasConversion&lt;string&gt;().HasMaxLength(16)）。
/// - Manual：用户在测试集页面手动新增的样本（半自动模式下"先跑一次→批准为期望"亦走此值）
/// - FromFailed：从 Media_Item.Status=Failed 历史灌入到暂存区的样本
/// </remarks>
public enum ParseTestCaseSource
{
    /// <summary>用户手动新增</summary>
    Manual = 1,

    /// <summary>从失败历史灌入</summary>
    FromFailed = 2,
}
