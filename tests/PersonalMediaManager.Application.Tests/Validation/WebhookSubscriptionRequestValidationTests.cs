using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Webhook;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>WebhookSubscription 请求 DTO 的边界声明式校验（A 类字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 WebhookSubscriptionService 内 NormalizeName / NormalizeUrl / ValidateTimeout 抛 BusinessException 的
/// 无状态字段校验（Name 非空 / 长度、Url 非空 / 长度 / http(s) 格式、TimeoutSeconds 范围），改为记录型 Request DTO
/// 「主构造参数」上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// Events 集合 / 逐元素校验（集合元素级）+ 重名 / 不存在（查 DB）+ Secret 解密失败（包外部异常）仍 KEEP 在 service。
/// 记录的校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// </remarks>
public sealed class WebhookSubscriptionRequestValidationTests
{
    private static readonly IReadOnlyList<string> SampleEvents = ["media.archived"];

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
            new CreateWebhookSubscriptionRequest("plex", "https://hooks.example.com/plex", null, SampleEvents));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_FailsWith_RequiredMessage(string name)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWebhookSubscriptionRequest(name, "https://hooks.example.com/plex", null, SampleEvents));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Name 不能为空"));
    }

    [Fact]
    public void Create_NameTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWebhookSubscriptionRequest(new string('n', 65), "https://hooks.example.com/plex", null, SampleEvents));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Name"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankUrl_FailsWith_RequiredMessage(string url)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWebhookSubscriptionRequest("plex", url, null, SampleEvents));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Url 不能为空"));
    }

    [Fact]
    public void Create_UrlTooLong_FailsWith_LengthMessage()
    {
        string longUrl = "https://example.com/" + new string('p', 500);
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWebhookSubscriptionRequest("plex", longUrl, null, SampleEvents));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("500"));
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("not-a-url")]
    [InlineData("example.com")]
    public void Create_NonHttpUrl_FailsWith_SchemeMessage(string url)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWebhookSubscriptionRequest("plex", url, null, SampleEvents));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("http / https"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Create_TimeoutOutOfRange_Fails(int timeoutSeconds)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateWebhookSubscriptionRequest(
                "plex", "https://hooks.example.com/plex", null, SampleEvents,
                Enabled: true, TimeoutSeconds: timeoutSeconds));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("TimeoutSeconds"));
    }

    [Fact]
    public void Update_NonHttpUrl_FailsWith_SchemeMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateWebhookSubscriptionRequest(1, "plex", "ftp://example.com", null, SampleEvents, true, 10));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("http / https"));
    }
}
