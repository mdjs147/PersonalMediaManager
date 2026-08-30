using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Watch;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>WatchFolder 请求 DTO 的边界声明式校验（A 类字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 WatchFolderService 内 NormalizePath / ValidatePriority / ValidateAlias 抛 BusinessException 的
/// 无状态字段校验，改为记录型 Request DTO「主构造参数」上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 记录的校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主
/// （端到端另由 Host.Tests/WatchFoldersTests 覆盖）。
/// </remarks>
public sealed class WatchFolderRequestValidationTests
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
            new CreateWatchFolderRequest("D:\\Media", "别名", false, false));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankPath_FailsWith_RequiredMessage(string path)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchFolderRequest(path, null, false, false));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("路径不能为空"));
    }

    [Fact]
    public void Create_PathTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchFolderRequest(new string('x', 501), null, false, false));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("500"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Create_PriorityOutOfRange_Fails(int priority)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchFolderRequest("D:\\Media", null, false, false, Enabled: true, Priority: priority));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Priority"));
    }

    [Fact]
    public void Create_AliasTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWatchFolderRequest("D:\\Media", new string('a', 65), false, false));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Alias"));
    }
}
