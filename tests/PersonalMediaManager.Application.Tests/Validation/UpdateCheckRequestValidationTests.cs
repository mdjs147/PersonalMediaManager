using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>升级检查相关请求 DTO 的边界声明式校验（A 类字段规则迁出 UpdateSettingService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 UpdateSettingService 内 owner/repo 非空、CheckIntervalHours 区间、version 非空抛 BusinessException 的
/// 无状态字段校验，改为记录主构造参数上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 「私有仓库 PAT」是跨字段+读 DB 既有值的条件式规则，仍留 service（不在此测）。
/// 本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// </remarks>
public sealed class UpdateCheckRequestValidationTests
{
    // 反射主构造参数上的 ValidationAttribute + 同名属性当前值，逐个执行并收集失败项
    private static IReadOnlyList<ValidationResult> Validate(object instance)
    {
        List<ValidationResult> results = [];
        Type type = instance.GetType();
        ConstructorInfo ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        foreach (ParameterInfo param in ctor.GetParameters())
        {
            PropertyInfo? prop = type.GetProperty(param.Name!);
            if (prop is null)
                continue;
            object? value = prop.GetValue(instance);
            ValidationContext context = new(instance) { MemberName = param.Name };
            foreach (ValidationAttribute attr in param.GetCustomAttributes<ValidationAttribute>())
            {
                ValidationResult? result = attr.GetValidationResult(value, context);
                if (result is not null)
                    results.Add(result);
            }
        }
        return results;
    }

    // ---------- UpdateSettingsRequest ----------

    [Fact]
    public void UpdateSettings_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateSettingsRequest("mdjs147", "PersonalMediaManager", GitHubPat: null, IsPrivateRepo: false));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateSettings_BlankOwner_Fails(string owner)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateSettingsRequest(owner, "PersonalMediaManager", null, false));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("GitHub Owner 不能为空"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateSettings_BlankRepo_Fails(string repo)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateSettingsRequest("mdjs147", repo, null, false));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("GitHub Repo 不能为空"));
    }

    [Fact]
    public void UpdateSettings_NullInterval_Passes()
    {
        // CheckIntervalHours 为 null（不变语义）应被 [Range] 放行，与原 is { } hours && 只在有值时校验一致
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateSettingsRequest("mdjs147", "PersonalMediaManager", null, false, Enabled: null, CheckIntervalHours: null));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(721)]
    public void UpdateSettings_IntervalOutOfRange_Fails(int hours)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateSettingsRequest("mdjs147", "PersonalMediaManager", null, false, Enabled: null, CheckIntervalHours: hours));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("检查周期必须在 1~720 小时之间"));
    }

    // ---------- TestUpdateCheckRequest ----------

    [Fact]
    public void TestRequest_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new TestUpdateCheckRequest("mdjs147", "PersonalMediaManager", GitHubPat: "ghp_x"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TestRequest_BlankOwner_Fails(string owner)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new TestUpdateCheckRequest(owner, "PersonalMediaManager", null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("GitHub Owner 不能为空"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TestRequest_BlankRepo_Fails(string repo)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new TestUpdateCheckRequest("mdjs147", repo, null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("GitHub Repo 不能为空"));
    }

    // ---------- SkipUpdateVersionRequest ----------

    [Fact]
    public void SkipVersion_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new SkipUpdateVersionRequest("0.2.0"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SkipVersion_BlankVersion_Fails(string version)
    {
        IReadOnlyList<ValidationResult> errors = Validate(new SkipUpdateVersionRequest(version));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("版本号不能为空"));
    }
}
