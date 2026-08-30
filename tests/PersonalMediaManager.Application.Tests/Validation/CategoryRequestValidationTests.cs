using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Category;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>Category 域请求 DTO 的边界声明式校验（A 类字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 CategoryDefinitionService（NormalizeName / NormalizeTargetRoot / ValidatePriority / ValidateDescription）
/// 与 CategoryMatchRuleService（ValidateName / ValidatePriority）内抛 BusinessException 的无状态字段校验，
/// 改为记录型 Request DTO「主构造参数」上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// CategoryMatchRule 的 Conditions（空 / 长度 / 必须是 JSON 对象 / 合法 JSON）按设计仍留在 service，故不在此覆盖
/// （端到端由 Host.Tests/CategoryMatchRulesTests 覆盖）。
/// </remarks>
public sealed class CategoryRequestValidationTests
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

    // ─── CategoryDefinition ───────────────────────────────────────────────

    [Fact]
    public void CreateDefinition_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest("电影", MediaType.Movie, "D:\\Plex\\Movies"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateDefinition_BlankName_FailsWith_RequiredMessage(string name)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest(name, MediaType.Movie, "D:\\Plex\\Movies"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("分类 Name 不能为空"));
    }

    [Fact]
    public void CreateDefinition_NameTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest(new string('x', 65), MediaType.Movie, "D:\\Plex\\Movies"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("64"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateDefinition_BlankTargetRoot_FailsWith_RequiredMessage(string targetRoot)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest("电影", MediaType.Movie, targetRoot));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("TargetRoot 不能为空"));
    }

    [Fact]
    public void CreateDefinition_TargetRootTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest("电影", MediaType.Movie, new string('x', 501)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("500"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void CreateDefinition_PriorityOutOfRange_Fails(int priority)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest("电影", MediaType.Movie, "D:\\Plex\\Movies", Priority: priority));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Priority"));
    }

    [Fact]
    public void CreateDefinition_DescriptionTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryDefinitionRequest("电影", MediaType.Movie, "D:\\Plex\\Movies", Description: new string('d', 201)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Description"));
    }

    [Fact]
    public void UpdateDefinition_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateCategoryDefinitionRequest(1, "电影", MediaType.Movie, "D:\\Plex\\Movies", 100, null, 1));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateDefinition_BlankName_FailsWith_RequiredMessage(string name)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateCategoryDefinitionRequest(1, name, MediaType.Movie, "D:\\Plex\\Movies", 100, null, 1));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("分类 Name 不能为空"));
    }

    // ─── CategoryMatchRule ────────────────────────────────────────────────

    [Fact]
    public void CreateRule_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryMatchRuleRequest(1, "默认", "{}"));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void CreateRule_NullName_PassesValidation()
    {
        // Name 可空，null 合法（MaxLength 对 null 放行）
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryMatchRuleRequest(1, null, "{}"));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void CreateRule_NameTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryMatchRuleRequest(1, new string('n', 101), "{}"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("100"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void CreateRule_PriorityOutOfRange_Fails(int priority)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateCategoryMatchRuleRequest(1, "默认", "{}", Priority: priority));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Priority"));
    }

    [Fact]
    public void UpdateRule_PriorityOutOfRange_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateCategoryMatchRuleRequest(1, 1, "默认", "{}", 10000, true, 1));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Priority"));
    }
}
