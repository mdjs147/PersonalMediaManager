using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Scan;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>DryRunRequest 请求 DTO 的边界声明式校验（空路径规则迁出 DryRunService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 DryRunService.PreviewAsync 内空路径抛 BusinessException「路径不能为空」，
/// 改为 DryRunRequest「主构造参数」上的 [RequiredNotBlank]，由 [ApiController] 在 [FromBody] 模型绑定阶段校验。
/// service 内「无法从路径解析文件名」是派生前置条件（Trim + Path.GetFileName 后判空），无法用单字段 DataAnnotations 表达，仍留 service。
/// 反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// </remarks>
public sealed class DryRunRequestValidationTests
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
    public void ValidPath_PassesValidation()
    {
        Validate(new DryRunRequest("F:\\Downloads\\movie.mkv")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPath_FailsWith_RequiredMessage(string path)
    {
        Validate(new DryRunRequest(path))
            .Should().Contain(e => e.ErrorMessage!.Contains("路径不能为空"));
    }
}
