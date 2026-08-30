using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.History;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>History 请求 DTO 的边界声明式校验（A 类字段规则迁出 HistoryService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 HistoryService 内 ListAsync / ListPendingAsync 的「分页参数非法」（page ≥ 1）无状态字段校验，
/// 改为记录型 Query DTO「主构造参数」上的 DataAnnotations，由 [ApiController] 模型绑定阶段校验。
/// 记录校验元数据须挂构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// From&gt;To（跨字段）/ 状态机 / 批量上限 / 记录不存在等仍为 KEEP（留在 service），不在本测试覆盖范围。
/// </remarks>
public sealed class HistoryRequestValidationTests
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
    public void HistoryListQuery_ValidPage_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new HistoryListQuery(Page: 1));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HistoryListQuery_PageLessThanOne_FailsWith_PageMessage(int page)
    {
        IReadOnlyList<ValidationResult> errors = Validate(new HistoryListQuery(Page: page));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("分页参数非法"));
    }

    [Fact]
    public void PendingListQuery_ValidPage_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new PendingListQuery(Page: 1));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PendingListQuery_PageLessThanOne_FailsWith_PageMessage(int page)
    {
        IReadOnlyList<ValidationResult> errors = Validate(new PendingListQuery(Page: page));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("分页参数非法"));
    }
}
