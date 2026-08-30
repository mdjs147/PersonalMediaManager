using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Logs;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>LogQuery 查询 DTO 的边界声明式校验（Page / PageSize 范围规则迁出 LogQueryService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 LogQueryService.ListAsync 内 Page&lt;1 / PageSize 越界 抛 BusinessException「分页参数非法」，
/// 改为 LogQuery「主构造参数」上的 [Range]，由 [ApiController] 在 [FromQuery] 模型绑定阶段校验。
/// Level（大小写不敏感）与 From&gt;To（跨字段）无法用单字段 DataAnnotations 表达，仍留 service，由 LogQueryServiceTests 覆盖。
/// 反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// </remarks>
public sealed class LogQueryRequestValidationTests
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
    public void Defaults_PassValidation()
    {
        Validate(new LogQuery()).Should().BeEmpty();
    }

    [Fact]
    public void PageBelowOne_Fails()
    {
        Validate(new LogQuery(Page: 0))
            .Should().Contain(e => e.ErrorMessage!.Contains("分页参数非法"));
    }

    [Fact]
    public void PageSizeZero_Fails()
    {
        Validate(new LogQuery(PageSize: 0))
            .Should().Contain(e => e.ErrorMessage!.Contains("分页参数非法"));
    }

    [Fact]
    public void PageSizeOverLimit_Fails()
    {
        Validate(new LogQuery(PageSize: 999))
            .Should().Contain(e => e.ErrorMessage!.Contains("分页参数非法"));
    }

    [Fact]
    public void PageSizeAtUpperBound_PassesValidation()
    {
        Validate(new LogQuery(PageSize: 200)).Should().BeEmpty();
    }
}
