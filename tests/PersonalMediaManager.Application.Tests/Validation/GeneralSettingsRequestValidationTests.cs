using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Dtos.Settings;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>通用设置 KV 更新项的边界声明式校验（受限 key 值区间 + 跨项关系）</summary>
/// <remarks>
/// 通用 KV 端点 Value 是字符串、合法区间随 Key 而定，故 UpdateGeneralItem 实现 IValidatableObject 而非用 [Range]。
/// 防止把告警阈值 / 抑制窗口 / 归档余量配成失效：百分比 [0,100]、抑制分钟 [0,10080]（7 天）、余量 MB [0,1048576]（1 TB）。
/// 跨项关系（UpdateGeneralRequest.Validate）：warn / critical 同次提交且均为正整数时须 critical ≤ warn。
/// 本测试直接驱动 IValidatableObject.Validate（等价 MVC 对集合元素 / 根对象的递归校验），无需起 Web 宿主。
/// 读侧另有 Math.Clamp 兜底（HealthCheckServiceTests.ParsePercentOr / AlertServiceTests 抑制上限测试覆盖）。
/// </remarks>
public sealed class GeneralSettingsRequestValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(UpdateGeneralItem item)
        => item.Validate(new ValidationContext(item)).ToList();

    private static IReadOnlyList<ValidationResult> ValidateRequest(params UpdateGeneralItem[] items)
    {
        UpdateGeneralRequest req = new(items);
        return req.Validate(new ValidationContext(req)).ToList();
    }

    // 受限 key：区间内 + 上下边界值放行
    [Theory]
    [InlineData("Archive_DiskWarnPercent", "0")]
    [InlineData("Archive_DiskWarnPercent", "100")]
    [InlineData("Archive_DiskWarnPercent", "37")]
    [InlineData("Archive_DiskCriticalPercent", "0")]
    [InlineData("Archive_DiskCriticalPercent", "100")]
    [InlineData("System_AlertSuppressMinutes", "0")]
    [InlineData("System_AlertSuppressMinutes", "10080")]
    [InlineData("System_AlertSuppressMinutes", "60")]
    public void RangedKey_InRange_Passes(string key, string value)
        => Validate(new UpdateGeneralItem(key, value)).Should().BeEmpty();

    // 百分比越界（>100 / <0）拦截 —— 防归档磁盘恒 fail + disk.low 告警风暴
    [Theory]
    [InlineData("Archive_DiskWarnPercent", "101")]
    [InlineData("Archive_DiskWarnPercent", "-1")]
    [InlineData("Archive_DiskCriticalPercent", "200")]
    [InlineData("Archive_DiskCriticalPercent", "-5")]
    public void Percent_OutOfRange_Fails(string key, string value)
        => Validate(new UpdateGeneralItem(key, value))
            .Should().Contain(e => e.ErrorMessage!.Contains("必须在 0~100 之间"));

    // 抑制窗口越界（>10080 / <0）拦截 —— 防同类告警进程内永久哑火
    [Theory]
    [InlineData("10081")]
    [InlineData("99999999")]
    [InlineData("-1")]
    public void SuppressMinutes_OutOfRange_Fails(string value)
        => Validate(new UpdateGeneralItem("System_AlertSuppressMinutes", value))
            .Should().Contain(e => e.ErrorMessage!.Contains("必须在 0~10080 之间"));

    // 归档余量 MB [0,1048576]（1 TB）：区间内放行
    [Theory]
    [InlineData("0")]
    [InlineData("1048576")]
    [InlineData("512000")]
    public void MinFreeSpaceMB_InRange_Passes(string value)
        => Validate(new UpdateGeneralItem("Archive_MinFreeSpaceMB", value)).Should().BeEmpty();

    // 归档余量越界（<0 / >1048576）拦截 —— 防误填超大余量把所有归档卡成「磁盘不足」
    [Theory]
    [InlineData("-1")]
    [InlineData("1048577")]
    [InlineData("99999999")]
    public void MinFreeSpaceMB_OutOfRange_Fails(string value)
        => Validate(new UpdateGeneralItem("Archive_MinFreeSpaceMB", value))
            .Should().Contain(e => e.ErrorMessage!.Contains("必须在 0~1048576 之间"));

    // 受限 key 但非整数 → 拦截
    [Theory]
    [InlineData("Archive_DiskWarnPercent", "abc")]
    [InlineData("System_AlertSuppressMinutes", "12.5")]
    [InlineData("Archive_MinFreeSpaceMB", "10GB")]
    public void RangedKey_NotInteger_Fails(string key, string value)
        => Validate(new UpdateGeneralItem(key, value))
            .Should().Contain(e => e.ErrorMessage!.Contains("必须为整数"));

    // 受限 key 空值 → 放行（读侧回退默认/不变，不在写侧拦）
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RangedKey_Blank_Passes(string? value)
        => Validate(new UpdateGeneralItem("Archive_DiskCriticalPercent", value)).Should().BeEmpty();

    // 非受限 key → 任意值放行（不波及其它 KV）
    [Theory]
    [InlineData("Archive_ConflictPolicy", "Overwrite")]
    [InlineData("Proxy_Host", "whatever")]
    [InlineData("Some_Unknown_Key", "99999999")]
    public void NonRangedKey_AnyValue_Passes(string key, string value)
        => Validate(new UpdateGeneralItem(key, value)).Should().BeEmpty();

    // ---------- warn / critical 跨项关系（UpdateGeneralRequest.Validate） ----------

    // 倒置（critical > warn）→ 拦截：剩余空间先低于警告值再低于严重值，数值上须 critical ≤ warn
    [Fact]
    public void Thresholds_Inverted_Fails()
        => ValidateRequest(
                new UpdateGeneralItem("Archive_DiskWarnPercent", "5"),
                new UpdateGeneralItem("Archive_DiskCriticalPercent", "50"))
            .Should().Contain(e => e.ErrorMessage!.Contains("不能大于警告阈值"));

    // 正确序（critical < warn）与相等均放行
    [Theory]
    [InlineData("10", "5")]
    [InlineData("10", "10")]
    [InlineData("100", "1")]
    public void Thresholds_Ordered_Passes(string warn, string critical)
        => ValidateRequest(
                new UpdateGeneralItem("Archive_DiskWarnPercent", warn),
                new UpdateGeneralItem("Archive_DiskCriticalPercent", critical))
            .Should().BeEmpty();

    // 任一为 0 = 该档检查关闭 → 不校验先后关系
    [Theory]
    [InlineData("0", "5")]
    [InlineData("10", "0")]
    [InlineData("0", "0")]
    public void Thresholds_EitherZero_Passes(string warn, string critical)
        => ValidateRequest(
                new UpdateGeneralItem("Archive_DiskWarnPercent", warn),
                new UpdateGeneralItem("Archive_DiskCriticalPercent", critical))
            .Should().BeEmpty();

    // 单 key 提交 → 不拦（通用 KV 逐项提交的现实约束；关系提示在两项运行时描述里）
    [Theory]
    [InlineData("Archive_DiskWarnPercent", "5")]
    [InlineData("Archive_DiskCriticalPercent", "50")]
    public void Thresholds_SingleKey_Passes(string key, string value)
        => ValidateRequest(new UpdateGeneralItem(key, value)).Should().BeEmpty();

    // 任一非整数 / 空 → 关系校验跳过（项级校验已拦非整数，避免重复报错）
    [Theory]
    [InlineData("abc", "50")]
    [InlineData("5", "")]
    public void Thresholds_NotInteger_Or_Blank_SkipsRelation(string warn, string critical)
        => ValidateRequest(
                new UpdateGeneralItem("Archive_DiskWarnPercent", warn),
                new UpdateGeneralItem("Archive_DiskCriticalPercent", critical))
            .Should().BeEmpty();

    // 同 key 重复出现 → 取最后一次（与服务端逐项 upsert「后写覆盖」语义一致）
    [Fact]
    public void Thresholds_DuplicateKey_LastWins()
    {
        // 最终 warn=60 ≥ critical=50 → 放行
        ValidateRequest(
                new UpdateGeneralItem("Archive_DiskWarnPercent", "5"),
                new UpdateGeneralItem("Archive_DiskCriticalPercent", "50"),
                new UpdateGeneralItem("Archive_DiskWarnPercent", "60"))
            .Should().BeEmpty();

        // 最终 warn=5 < critical=50 → 拦截
        ValidateRequest(
                new UpdateGeneralItem("Archive_DiskWarnPercent", "60"),
                new UpdateGeneralItem("Archive_DiskCriticalPercent", "50"),
                new UpdateGeneralItem("Archive_DiskWarnPercent", "5"))
            .Should().Contain(e => e.ErrorMessage!.Contains("不能大于警告阈值"));
    }
}
