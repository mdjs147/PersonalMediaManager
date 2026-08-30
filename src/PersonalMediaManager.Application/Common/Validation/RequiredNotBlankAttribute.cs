using System.ComponentModel.DataAnnotations;

namespace PersonalMediaManager.Application.Common.Validation;

/// <summary>必填且非空白字符串校验</summary>
/// <remarks>
/// DataAnnotations 内置 <see cref="RequiredAttribute"/> 把纯空白串（"   "）判为合法，
/// 而本项目历史校验一律用 <c>string.IsNullOrWhiteSpace</c>；为对齐既有语义、避免「档 2 边界校验」
/// 放过纯空白输入，引入本特性统一「不能为空」判定。
/// 它是大量业务异常（A 类无状态字段校验）边界化后最高频复用的字段规则，故独立成可复用特性。
/// 用法（直接挂记录主构造参数，**勿用** <c>[property:]</c>，否则 MVC 对 record 校验会抛 InvalidOperationException）：
/// <c>record Foo([RequiredNotBlank(ErrorMessage = "路径不能为空")] string Path);</c>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class RequiredNotBlankAttribute : ValidationAttribute
{
    /// <summary>null 或纯空白判为非法，其余合法</summary>
    public override bool IsValid(object? value) =>
        value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;
}
