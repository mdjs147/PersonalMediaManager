using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Tmdb;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>UpdateTmdbSettingRequest 的边界声明式校验（A 类字段规则迁出 TmdbSettingService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 TmdbSettingService 内 ValidateLanguage / ValidateInt 抛 BusinessException 的无状态字段校验，
/// 改为记录主构造参数上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 单权重 [0,1] 区间亦迁为四个权重参数上的 [Range(0.0,1.0)]；「四项之和≈1」仍是跨字段规则，留 service（不在此测）。
/// 本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// </remarks>
public sealed class TmdbSettingRequestValidationTests
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

    // 合法基线：权重之和=1（跨字段规则不在 DTO 注解内，本测试只验单字段边界）
    private static UpdateTmdbSettingRequest Valid() =>
        new(ApiKey: null,
            Language: "zh-CN",
            FallbackLanguage: "en-US",
            CandidateThreshold: 3,
            RateLimitPerSecond: 40,
            MetadataCacheHours: 24,
            SearchCacheMinutes: 60,
            ScoreWeightTitle: 0.5,
            ScoreWeightYear: 0.3,
            ScoreWeightPopularity: 0.1,
            ScoreWeightLanguage: 0.1);

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid());
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLanguage_FailsWith_RequiredMessage(string language)
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { Language = language });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("语言不能为空"));
    }

    [Fact]
    public void LanguageTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { Language = new string('x', 17) });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("16"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFallbackLanguage_FailsWith_RequiredMessage(string fallback)
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { FallbackLanguage = fallback });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("兜底语言不能为空"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void CandidateThresholdOutOfRange_Fails(int value)
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { CandidateThreshold = value });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("候选阈值"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void RateLimitPerSecondOutOfRange_Fails(int value)
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { RateLimitPerSecond = value });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("每秒速率限制"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(721)]
    public void MetadataCacheHoursOutOfRange_Fails(int value)
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { MetadataCacheHours = value });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("元数据缓存"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10081)]
    public void SearchCacheMinutesOutOfRange_Fails(int value)
    {
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { SearchCacheMinutes = value });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("搜索缓存"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ScoreWeightOutOfRange_Fails(double weight)
    {
        // 任取一个权重越界即应命中其对应 [Range(0.0,1.0)]（此处校验标题权重）
        IReadOnlyList<ValidationResult> errors = Validate(Valid() with { ScoreWeightTitle = weight });
        errors.Should().Contain(e => e.ErrorMessage!.Contains("标题权重"));
    }
}
