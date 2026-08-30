using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Review;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>Review 请求 DTO 的边界声明式校验（A 类字段规则迁出 ReviewService 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 ReviewService 内 ListAsync「page 必须 ≥ 1」、TmdbSearchAsync「搜索词不能为空」
/// 两处无状态字段校验，改为记录型 Query DTO「主构造参数」上的 DataAnnotations，由 [ApiController] 模型绑定阶段校验。
/// 记录校验元数据须挂构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主。
/// mediaType / items 集合 / 季集 / 记录不存在 / TMDB 异常等仍为 KEEP（留在 service），不在本测试覆盖范围。
/// </remarks>
public sealed class ReviewRequestValidationTests
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
    public void ReviewListQuery_ValidPage_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new ReviewListQuery(Page: 1));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReviewListQuery_PageLessThanOne_FailsWith_PageMessage(int page)
    {
        IReadOnlyList<ValidationResult> errors = Validate(new ReviewListQuery(Page: page));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("page 必须 ≥ 1"));
    }

    [Fact]
    public void TmdbSearchListQuery_ValidQuery_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new TmdbSearchListQuery("盗梦空间"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TmdbSearchListQuery_BlankQuery_FailsWith_RequiredMessage(string query)
    {
        IReadOnlyList<ValidationResult> errors = Validate(new TmdbSearchListQuery(query));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("搜索词不能为空"));
    }

    // ---------- 批量集合校验（items 空 / 超上限，迁自 ReviewService 直测） ----------

    private static BatchConfirmItem ConfirmItem() => new(0, 1, "movie", 1, null, 2020, null, null, 0);

    private static CheckFileItem CheckItem() => new(0, 0);

    [Fact]
    public void BatchConfirmRequest_EmptyItems_FailsWith_RequiredMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new BatchConfirmRequest([]));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("items 不能为空"));
    }

    [Fact]
    public void BatchConfirmRequest_OverLimit_FailsWith_LimitMessage()
    {
        List<BatchConfirmItem> items = [.. Enumerable.Range(0, 51).Select(_ => ConfirmItem())];
        IReadOnlyList<ValidationResult> errors = Validate(new BatchConfirmRequest(items));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("超过上限 50"));
    }

    [Fact]
    public void BatchConfirmRequest_ValidSingle_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new BatchConfirmRequest([ConfirmItem()]));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void PreviewPathsRequest_EmptyItems_FailsWith_RequiredMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(new ReviewPreviewPathRequest([]));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("items 不能为空"));
    }

    [Fact]
    public void CheckFilesRequest_OverLimit_FailsWith_LimitMessage()
    {
        List<CheckFileItem> items = [.. Enumerable.Range(0, 501).Select(_ => CheckItem())];
        IReadOnlyList<ValidationResult> errors = Validate(new CheckFilesRequest(items));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("超过上限 500"));
    }
}
