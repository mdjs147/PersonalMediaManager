namespace PersonalMediaManager.Application.Contracts;

/// <summary>密码哈希契约（BCrypt cost=10，详见 Infrastructure.Platform/Security/PasswordHasher）</summary>
public interface IPasswordHasher
{
    string Hash(string plain);
    bool Verify(string plain, string hash);
}
