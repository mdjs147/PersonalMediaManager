using System.ComponentModel.DataAnnotations;
using System.Reflection;
using PersonalMediaManager.Application.Dtos.Account;
using PersonalMediaManager.Application.Dtos.Auth;
using PersonalMediaManager.Application.Dtos.Setup;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Tests.Validation;

/// <summary>Auth / Account / Setup 请求 DTO 的边界声明式校验（A 类字段规则迁出 service 后落点）</summary>
/// <remarks>
/// 档 2 改造：原 AuthService / AccountService / SetupService 内「用户名非空 / 密码长度」抛 BusinessException 的
/// 无状态字段校验，改为记录型 Request DTO「主构造参数」上的 DataAnnotations，由 [ApiController] 在模型绑定阶段校验。
/// 校验元数据须挂在构造参数（而非属性，否则 MVC 抛 InvalidOperationException）；
/// 故本测试反射主构造参数上的校验特性 + 同名属性当前值逐个执行，等价模拟 MVC 边界校验，无需起 Web 宿主
/// （端到端另由 Host.Tests 的 Setup/Auth/Account E2E 覆盖）。
/// 「用户名已存在 / 用户不存在 / 旧密码错误 / 不能删除最后一个管理员」等 DB/业务校验仍留 service，不在此覆盖。
/// </remarks>
public sealed class AuthAccountRequestValidationTests
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

    // ---------- Auth LoginRequest ----------

    [Fact]
    public void Login_ValidRequest_PassesValidation()
    {
        Validate(new LoginRequest("admin", "secret123")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Login_BlankUsername_FailsWith_RequiredMessage(string username)
    {
        Validate(new LoginRequest(username, "secret123"))
            .Should().Contain(e => e.ErrorMessage!.Contains("用户名不能为空"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Login_BlankPassword_FailsWith_RequiredMessage(string password)
    {
        Validate(new LoginRequest("admin", password))
            .Should().Contain(e => e.ErrorMessage!.Contains("密码不能为空"));
    }

    // ---------- Account CreateUserRequest ----------

    [Fact]
    public void CreateUser_ValidRequest_PassesValidation()
    {
        Validate(new CreateUserRequest("viewer1", "secret123", UserRole.Viewer)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateUser_BlankUsername_Fails(string username)
    {
        Validate(new CreateUserRequest(username, "secret123", UserRole.Viewer))
            .Should().Contain(e => e.ErrorMessage!.Contains("用户名不能为空"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateUser_BlankPassword_FailsWith_RequiredMessage(string password)
    {
        Validate(new CreateUserRequest("viewer1", password, UserRole.Viewer))
            .Should().Contain(e => e.ErrorMessage!.Contains("密码不能为空"));
    }

    [Fact]
    public void CreateUser_PasswordTooShort_FailsWith_LengthMessage()
    {
        Validate(new CreateUserRequest("viewer1", "12345", UserRole.Viewer))
            .Should().Contain(e => e.ErrorMessage!.Contains("密码长度至少 6 位"));
    }

    [Fact]
    public void CreateUser_PasswordExactlySix_PassesValidation()
    {
        Validate(new CreateUserRequest("viewer1", "123456", UserRole.Viewer)).Should().BeEmpty();
    }

    // ---------- Account ChangePasswordRequest ----------

    [Fact]
    public void ChangePassword_ValidRequest_PassesValidation()
    {
        Validate(new ChangePasswordRequest("oldpass", "newsecret123")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangePassword_BlankNewPassword_FailsWith_RequiredMessage(string newPassword)
    {
        Validate(new ChangePasswordRequest("oldpass", newPassword))
            .Should().Contain(e => e.ErrorMessage!.Contains("新密码不能为空"));
    }

    [Fact]
    public void ChangePassword_NewPasswordTooShort_FailsWith_LengthMessage()
    {
        Validate(new ChangePasswordRequest("oldpass", "12345"))
            .Should().Contain(e => e.ErrorMessage!.Contains("新密码长度至少 6 位"));
    }

    [Fact]
    public void ChangePassword_BlankOldPassword_DoesNotFailValidation()
    {
        // OldPassword 不加边界校验：空旧密码在 service 走 _hasher.Verify → 「旧密码错误」（KEEP），非边界字段校验
        Validate(new ChangePasswordRequest("", "newsecret123")).Should().BeEmpty();
    }

    // ---------- Setup CreateAdminRequest ----------

    [Fact]
    public void CreateAdmin_ValidRequest_PassesValidation()
    {
        Validate(new CreateAdminRequest("admin", "secret123")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAdmin_BlankUsername_Fails(string username)
    {
        Validate(new CreateAdminRequest(username, "secret123"))
            .Should().Contain(e => e.ErrorMessage!.Contains("用户名不能为空"));
    }

    [Fact]
    public void CreateAdmin_PasswordTooShort_FailsWith_LengthMessage()
    {
        Validate(new CreateAdminRequest("admin", "12345"))
            .Should().Contain(e => e.ErrorMessage!.Contains("密码长度至少 6 位"));
    }
}
