using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>JWT 颁发 / 验证契约（需求文档 §3.12）</summary>
/// <remarks>
/// HS256 + 30 天有效期；签名密钥来自 IJwtSigningKeyProvider（首次启动自动写文件兜底）。
/// 实现放 Infrastructure.Platform（§0.5 红线：Application 仅 Microsoft.Extensions.*，不引 IdentityModel.Tokens.Jwt）。
/// </remarks>
public interface IJwtTokenService
{
    /// <summary>颁发 token（按 claims 字段，避免 Application 依赖 UserAccount 内部 setter）</summary>
    string Generate(long userId, string username, UserRole role);

    /// <summary>距 exp 剩余时间 &lt; 7 天 → 应续签</summary>
    bool ShouldRefresh(DateTimeOffset expiresAt);
}

/// <summary>JWT 签名密钥提供者（首次启动随机生成 + 持久化到 AppPaths 下 jwt-signing-key.txt）</summary>
public interface IJwtSigningKeyProvider
{
    /// <summary>HS256 密钥（base64 字符串，至少 32 字节 = 256 位）</summary>
    string GetSigningKey();
}
