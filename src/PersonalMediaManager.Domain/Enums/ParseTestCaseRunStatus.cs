namespace PersonalMediaManager.Domain.Enums;

/// <summary>解析测试用例最近一次回归运行结果（Parse_TestCase.LastRunStatus）</summary>
/// <remarks>
/// EF 配置为字符串列（HasConversion&lt;string&gt;().HasMaxLength(16)）。
/// - NotRun：用例新增后尚未跑过；或期望基线未确认前默认值
/// - Pass：实测解析结果与期望基线全部字段一致
/// - Fail：实测与期望任一字段不一致；或解析过程抛异常
/// </remarks>
public enum ParseTestCaseRunStatus
{
    /// <summary>尚未运行</summary>
    NotRun = 0,

    /// <summary>实测与期望一致</summary>
    Pass = 1,

    /// <summary>实测与期望不一致或抛异常</summary>
    Fail = 2,
}
