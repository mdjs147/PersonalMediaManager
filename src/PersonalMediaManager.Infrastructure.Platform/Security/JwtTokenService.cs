using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Platform.Security;

/// <summary>JWT 颁发 / 续签判定（HS256，7 天 + 滑动续签）</summary>
/// <remarks>
/// Claims：userId / username / role / iat / sub / exp（按需求文档 §3.12）。
/// 实现放 Infrastructure.Platform（§0.5 Application 白名单仅 Microsoft.Extensions.*）；接口 IJwtTokenService 仍在 Application 层。
/// </remarks>
public sealed class JwtTokenService : IJwtTokenService
{
    // Token 有效期收紧为 7 天（原 30 天泄露窗口过大）；活跃用户在剩余 RefreshThreshold 内自动滑动续签，基本无感。
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan RefreshThreshold = TimeSpan.FromDays(3);
    public const string Issuer = "PersonalMediaManager";
    public const string Audience = "PersonalMediaManager.WebUI";

    private readonly IJwtSigningKeyProvider _keyProvider;
    private readonly IClock _clock;

    public JwtTokenService(IJwtSigningKeyProvider keyProvider, IClock clock)
    {
        _keyProvider = keyProvider;
        _clock = clock;
    }

    public string Generate(long userId, string username, UserRole role)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset exp = now + TokenLifetime;

        byte[] keyBytes = Convert.FromBase64String(_keyProvider.GetSigningKey());
        SigningCredentials credentials = new(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);

        Claim[] claims =
        [
            new("userId", userId.ToString()),
            new("username", username),
            new("role", role.ToString()),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
        ];

        JwtSecurityToken token = new(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: exp.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool ShouldRefresh(DateTimeOffset expiresAt) =>
        expiresAt - _clock.UtcNow < RefreshThreshold;
}
