using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Settings;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>MediaExtension 请求 DTO 的边界声明式校验（A 类字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 MediaExtensionService.NormalizeExtension / ValidateDescription 内抛 BusinessException 的
/// 无状态字段校验（非空 / 以 '.' 开头 / 不含空白或路径分隔符 / 最短 2 / 最长 32 / 说明长度），
/// 改为记录型 Request DTO「主构造参数」上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// 注意：校验在归一前生效，故针对的是用户原始输入；service 仍保留 Trim + ToLowerInvariant 归一（小写折叠入库）。
/// </remarks>
public sealed class MediaExtensionRequestValidationTests
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
            new CreateMediaExtensionRequest(".mkv", "Matroska", true));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankExtension_FailsWith_RequiredMessage(string ext)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateMediaExtensionRequest(ext, null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("扩展名不能为空"));
    }

    [Theory]
    [InlineData("mkv")]      // 不以 '.' 开头
    [InlineData(". mkv")]    // 含空白
    [InlineData(".mk v")]    // 含空白
    [InlineData(".mk\\v")]   // 含反斜杠
    [InlineData(".mk/v")]    // 含正斜杠
    public void Create_BadFormat_FailsWith_FormatMessage(string ext)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateMediaExtensionRequest(ext, null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("扩展名格式非法"));
    }

    [Fact]
    public void Create_TooShort_FailsWith_MinLengthMessage()
    {
        // 单独的 "." 只有 1 字符：触发 MinLength（同时也会被正则否决，断言取 MinLength 文案）
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateMediaExtensionRequest(".", null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("至少 2 个字符"));
    }

    [Fact]
    public void Create_TooLong_FailsWith_MaxLengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateMediaExtensionRequest("." + new string('a', 32), null)); // 总长 33
        errors.Should().Contain(e => e.ErrorMessage!.Contains("32"));
    }

    [Fact]
    public void Create_DescriptionTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateMediaExtensionRequest(".mkv", new string('d', 201)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("说明长度"));
    }

    [Fact]
    public void Update_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateMediaExtensionRequest(1, ".mkv", null, false));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("mkv")]
    public void Update_BadExtension_Fails(string ext)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateMediaExtensionRequest(1, ext, null, true));
        errors.Should().NotBeEmpty();
    }
}
