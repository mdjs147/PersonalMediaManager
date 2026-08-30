using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.Security;

/// <summary>BCrypt cost=10 密码哈希器（封装 BCrypt.Net-Next）</summary>
/// <remarks>
/// cost=10 ≈ 100ms 验证耗时；登录失败不锁定（需求文档 §3.12），仅写 Audit_Operation。
/// Verify 在 hash 损坏或非 BCrypt 字符串时返 false，不抛异常。
/// </remarks>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int Cost = 10;

    public string Hash(string plain) =>
        BCrypt.Net.BCrypt.HashPassword(plain, workFactor: Cost);

    public bool Verify(string plain, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(plain, hash);
        }
        catch
        {
            return false;
        }
    }
}
