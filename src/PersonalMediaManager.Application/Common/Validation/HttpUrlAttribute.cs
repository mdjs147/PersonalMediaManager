using System.ComponentModel.DataAnnotations;

namespace PersonalMediaManager.Application.Common.Validation;

/// <summary>http / https 绝对 URL 校验</summary>
/// <remarks>
/// 校验值为合法的绝对 URL 且 scheme 为 http 或 https。
/// 空值（null / 空白）放行——是否必填交由 <see cref="RequiredNotBlankAttribute"/> 单独表达，本特性只管「非空值的格式」，职责单一。
/// 复刻各 service 内 <c>Uri.TryCreate(s, UriKind.Absolute, out uri) &amp;&amp; (uri.Scheme is http/https)</c> 逻辑，
/// 供 Webhook Url / AiProvider BaseUrl 等字段复用。
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class HttpUrlAttribute : ValidationAttribute
{
    /// <summary>null/空白放行；非空值须为 http/https 绝对 URL</summary>
    public override bool IsValid(object? value)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return true;
        return Uri.TryCreate(s, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
