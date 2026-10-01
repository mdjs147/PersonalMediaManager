using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.Security;

/// <summary>JWT 签名密钥提供者：首次启动若 Jwt:SigningKey 不存在 → 256 位随机生成并持久化</summary>
/// <remarks>
/// 优先来源：①IConfiguration「Jwt:SigningKey」②AppPaths 下 jwt-signing-key.txt 兜底文件。
/// 单例 + 启动时一次性解析；线程安全。
/// 文件通过同目录原子发布避免多进程生成不同密钥；Unix 创建权限固定为 0600。
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
        // 跨进程锁覆盖“检查存在 + 发布”，不依赖 File.Move 的竞争失败语义。
        using FileStream lease = AcquireLease(keyFile + ".lock");
        _key = File.Exists(keyFile) ? ReadExisting(keyFile) : GenerateAndPersist(keyFile);
    }

    public string GetSigningKey() => _key;

    private static FileStream AcquireLease(string path)
    {
        PrivateFileSystem.RejectSymbolicLink(path);
        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileSystem.FilePermissions;
        long deadline = Environment.TickCount64 + 10000;
        while (true)
        {
            try { return new FileStream(path, options); }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static string GenerateAndPersist(string keyFile)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32); // 256 位
        string base64 = Convert.ToBase64String(bytes);
        string temporary = keyFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream file = PrivateFileSystem.CreateNew(temporary))
            {
                file.Write(global::System.Text.Encoding.UTF8.GetBytes(base64));
                file.Flush(flushToDisk: true);
            }

            try
            {
                // 仅发布完整文件且不覆盖；竞争失败者读取获胜进程的密钥。
                File.Move(temporary, keyFile, overwrite: false);
                return base64;
            }
            catch (IOException) when (File.Exists(keyFile))
            {
                return ReadExisting(keyFile);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ReadExisting(string keyFile)
    {
        PrivateFileSystem.RestrictFile(keyFile);
        string key = File.ReadAllText(keyFile).Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException($"JWT 签名密钥文件为空，请检查并恢复原密钥：{keyFile}");
        return key;
    }
}
