using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Watch;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>WatchIgnoreRule 请求 DTO 的边界声明式校验（A 类字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 WatchIgnoreRuleService 内 NormalizePattern / ValidateDescription 抛 BusinessException 的
/// 无状态字段校验（Pattern 非空 / Pattern 长度 / Description 长度），改为记录型 Request DTO「主构造参数」上的
/// DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// Extension 专属三条（依赖 Type==Extension 的跨字段规则）+ 同 Type+Pattern 唯一（查 DB）仍 KEEP 在 service。
/// 记录的校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// </remarks>
public sealed class WatchIgnoreRuleRequestValidationTests
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

    [Fact]
    public void Create_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchIgnoreRuleRequest(IgnoreRuleType.Keyword, "sample", "样片"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankPattern_FailsWith_RequiredMessage(string pattern)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchIgnoreRuleRequest(IgnoreRuleType.Keyword, pattern, null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Pattern 不能为空"));
    }

    [Fact]
    public void Create_PatternTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchIgnoreRuleRequest(IgnoreRuleType.Keyword, new string('x', 201), null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("200"));
    }

    [Fact]
    public void Create_DescriptionTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchIgnoreRuleRequest(IgnoreRuleType.Keyword, "sample", new string('d', 201)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Description"));
    }

    [Fact]
    public void Update_BlankPattern_FailsWith_RequiredMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateWatchIgnoreRuleRequest(1, IgnoreRuleType.Keyword, "   ", null, true));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Pattern 不能为空"));
    }
}
