using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.Security;

/// <summary>JWT 签名密钥提供者：首次启动若 Jwt:SigningKey 不存在 → 256 位随机生成并持久化</summary>
/// <remarks>
/// 优先来源：①IConfiguration「Jwt:SigningKey」②AppPaths 下 jwt-signing-key.txt 兜底文件。
/// 单例 + 启动时一次性解析；线程安全。
/// 写回 appsettings.json 是开发期约定（需求文档 §3.12），生产环境可由用户/部署脚本预置。
/// </remarks>
public sealed class SigningKeyProvider : IJwtSigningKeyProvider
{
    private readonly string _key;

    public SigningKeyProvider(IConfiguration configuration, AppPaths paths)
    {
        string? configured = configuration["Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = configured;
            return;
        }

        string keyFile = Path.Combine(paths.Root, "jwt-signing-key.txt");
        if (File.Exists(keyFile))
        {
            _key = File.ReadAllText(keyFile).Trim();
            return;
        }

        _key = GenerateAndPersist(keyFile);
    }

    public string GetSigningKey() => _key;

    private static string GenerateAndPersist(string keyFile)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32); // 256 位
        string base64 = Convert.ToBase64String(bytes);
        File.WriteAllText(keyFile, base64);
        return base64;
    }
}
