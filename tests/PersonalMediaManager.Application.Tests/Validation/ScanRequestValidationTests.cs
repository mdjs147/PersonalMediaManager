using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Scan;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>Scan 请求 DTO 的边界声明式校验（A 类字段规则迁出 ScanService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 ScanService.ScanPathAsync 的「路径不能为空」无状态字段校验，
/// 改为 ScanPathRequest「主构造参数」上的 DataAnnotations，由 [ApiController] 模型绑定阶段校验。
/// （ScanPathAsync 接口签名仍收 string，controller 仍传 request.Path；service 内仅保留对 null 的防御性归一。）
/// 记录校验元数据须挂构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// 未配置监控目录 / 目录不存在 / 已禁用 / 不可达 / 不是视频文件 / 已有扫描在进行等仍为 KEEP（留在 service）。
/// </remarks>
public sealed class ScanRequestValidationTests
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
    public void ScanPathRequest_ValidPath_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new ScanPathRequest("F:\\Downloads\\movie.mkv"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScanPathRequest_BlankPath_FailsWith_RequiredMessage(string path)
    {
        IReadOnlyList<ValidationResult> errors = Validate(new ScanPathRequest(path));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("路径不能为空"));
    }
}
