using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Platform.Security;

namespace PersonalMediaManager.Host.Security;

/// <summary>JwtBearer 选项后置配置器（r1 P3-5）：替代 PmmHost 内 BuildServiceProvider 反模式</summary>
/// <remarks>
/// 在 AddJwtBearer 注册后由 Options 框架在首次解析 JwtBearerOptions 时回调 PostConfigure，
/// 此时 ServiceProvider 已构造完成，可安全注入 IJwtSigningKeyProvider 获取签名密钥。
/// 旧实现 builder.Services.BuildServiceProvider() 是 ASP.NET 反模式：会丢失 Scoped 生命周期 +
/// 二次构造容器浪费资源 + 与后续 servicesOverride 不一致。改 PostConfigure 后无副作用。
/// </remarks>
internal sealed class JwtBearerOptionsConfigurator : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly IJwtSigningKeyProvider _keyProvider;

    public JwtBearerOptionsConfigurator(IJwtSigningKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;

        byte[] keyBytes = Convert.FromBase64String(_keyProvider.GetSigningKey());
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = JwtTokenService.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ClockSkew = TimeSpan.FromMinutes(1),
            // 让 ClaimsPrincipal.IsInRole / RequireRole 直接读 "role" claim
            RoleClaimType = "role",
            NameClaimType = "username",
        };
    }
}
