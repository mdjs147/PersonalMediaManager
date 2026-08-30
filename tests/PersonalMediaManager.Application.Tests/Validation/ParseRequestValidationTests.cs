using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>Parse 域请求 DTO 的边界声明式校验（A 类无状态字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 ParseRuleService / ParseAiProviderService / ParseTestCaseService 内抛 BusinessException 的
/// A 类无状态字段校验（必填/非空白、长度、数值范围、http(s) URL），改为记录型 Request DTO「主构造参数」上的
/// DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 记录的校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主
/// （端到端另由 Host.Tests/Settings/Parse* 覆盖）。
/// KEEP 在 service 的规则（重名/不存在/并发/正则编译/JSON 合法性/DefaultType 取值集/ExpectedMediaType≠Both）
/// 不在本测试范围——它们依赖 DB 或有状态判定，仍走 BusinessException + 集成测试。
/// </remarks>
public sealed class ParseRequestValidationTests
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

    // ── ParseRule：Create / Update（DefaultType 取值集 KEEP 在 service，不在此测） ──────────────

    [Fact]
    public void CreateParseRule_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest("标准电影", ParseScope.FileName, @"(?<title>.+?)\.(?<year>\d{4})"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateParseRule_BlankName_FailsWith_RequiredMessage(string name)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest(name, ParseScope.FileName, ".*"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("规则 Name 不能为空"));
    }

    [Fact]
    public void CreateParseRule_NameTooLong_FailsWith_LengthMessage()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest(new string('x', 101), ParseScope.FileName, ".*"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("100"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateParseRule_BlankPattern_Fails(string pattern)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest("规则", ParseScope.FileName, pattern));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Pattern 不能为空"));
    }

    [Fact]
    public void CreateParseRule_PatternTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest("规则", ParseScope.FileName, new string('a', 1001)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("1000"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void CreateParseRule_PriorityOutOfRange_Fails(int priority)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest("规则", ParseScope.FileName, ".*", Priority: priority));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Priority"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0.31)]
    public void CreateParseRule_ConfidenceBonusOutOfRange_Fails(double bonus)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest("规则", ParseScope.FileName, ".*", ConfidenceBonus: bonus));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("ConfidenceBonus"));
    }

    [Fact]
    public void CreateParseRule_DescriptionTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseRuleRequest("规则", ParseScope.FileName, ".*", Description: new string('d', 501)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Description"));
    }

    [Fact]
    public void UpdateParseRule_BlankNameAndPattern_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateParseRuleRequest(1, "  ", ParseScope.FileName, "", null, false, 100, 0.0, true, null, 0));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("规则 Name 不能为空"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Pattern 不能为空"));
    }

    // ── ParseAiProvider：Create / Update（重名/不存在 KEEP；BaseUrl 用 [HttpUrl]） ──────────────

    [Fact]
    public void CreateAiProvider_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "deepseek", AiProviderType.OpenAiCompatible, "https://api.deepseek.com", null, "deepseek-chat"));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAiProvider_BlankName_Fails(string name)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                name, AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Name 不能为空"));
    }

    [Fact]
    public void CreateAiProvider_NameTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                new string('n', 65), AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("64"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    public void CreateAiProvider_NonHttpBaseUrl_FailsWith_UrlMessage(string baseUrl)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, baseUrl, null, "llama3"));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("URL"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAiProvider_BlankBaseUrl_FailsWith_RequiredMessage(string baseUrl)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, baseUrl, null, "llama3"));
        // 空白先被 [RequiredNotBlank] 拦下（[HttpUrl] 对空白放行）
        errors.Should().Contain(e => e.ErrorMessage!.Contains("BaseUrl 不能为空"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAiProvider_BlankModel_Fails(string model)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, "http://127.0.0.1:11434", null, model));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Model 不能为空"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void CreateAiProvider_PriorityOutOfRange_Fails(int priority)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3", Priority: priority));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Priority"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void CreateAiProvider_TimeoutOutOfRange_Fails(int timeout)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3", TimeoutSeconds: timeout));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("TimeoutSeconds"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void CreateAiProvider_ConfidenceThresholdOutOfRange_Fails(double threshold)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3", ConfidenceThreshold: threshold));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("满意度阈值"));
    }

    [Fact]
    public void CreateAiProvider_NullConfidenceThreshold_PassesValidation()
    {
        // null 留空合法（回退全局阈值）：[Range] 对 null 自动放行
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3", ConfidenceThreshold: null));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void CreateAiProvider_ExtraOptionsTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseAiProviderRequest(
                "p", AiProviderType.Ollama, "http://127.0.0.1:11434", null, "llama3",
                ExtraOptions: new string('x', 2001)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("ExtraOptions"));
    }

    [Fact]
    public void UpdateAiProvider_NonHttpBaseUrl_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateParseAiProviderRequest(
                1, "p", AiProviderType.Ollama, "not-a-url", null, "llama3", false, 100, true, 30, null));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("URL"));
    }

    // ── ParseTestCase：Create / Update（重名/并发 KEEP；ExpectedMediaType≠Both KEEP 在 service） ──

    [Fact]
    public void CreateTestCase_ValidRequest_PassesValidation()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\迅雷下载\Inception.2010.mkv", ExpectedMediaType: MediaType.Movie));
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateTestCase_BlankSamplePath_FailsWith_RequiredMessage(string samplePath)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(samplePath));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("样本路径不能为空"));
    }

    [Fact]
    public void CreateTestCase_SamplePathTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(new string('x', 1001)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("样本路径长度"));
    }

    [Fact]
    public void CreateTestCase_WatchRootPathTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", WatchRootPath: new string('w', 1001)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("WatchRootPath"));
    }

    [Fact]
    public void CreateTestCase_ExpectedTitleTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", ExpectedTitle: new string('t', 301)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("ExpectedTitle"));
    }

    [Theory]
    [InlineData(1799)]
    [InlineData(2201)]
    public void CreateTestCase_ExpectedYearOutOfRange_Fails(int year)
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", ExpectedYear: year));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("ExpectedYear"));
    }

    [Fact]
    public void CreateTestCase_NullExpectedYear_PassesValidation()
    {
        // null 留空合法：[Range] 对 null 自动放行
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", ExpectedYear: null));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void CreateTestCase_NegativeExpectedSeason_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", ExpectedSeason: -1));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("ExpectedSeason"));
    }

    [Fact]
    public void CreateTestCase_NegativeExpectedEpisode_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", ExpectedEpisode: -1));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("ExpectedEpisode"));
    }

    [Fact]
    public void CreateTestCase_NotesTooLong_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new CreateParseTestCaseRequest(@"F:\a.mkv", Notes: new string('n', 501)));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("Notes"));
    }

    [Fact]
    public void UpdateTestCase_BlankSamplePath_Fails()
    {
        IReadOnlyList<ValidationResult> errors = Validate(
            new UpdateParseTestCaseRequest(1, "  ", null, null, null, null, null, null, null, 0));
        errors.Should().Contain(e => e.ErrorMessage!.Contains("样本路径不能为空"));
    }
}
