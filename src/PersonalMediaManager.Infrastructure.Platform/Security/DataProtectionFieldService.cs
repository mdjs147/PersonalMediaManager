using Microsoft.AspNetCore.DataProtection;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.Security;

/// <summary>IProtectedFieldService 的 DataProtection 实现</summary>
/// <remarks>
/// Purpose = "PmmField"：DataProtection 同实例下用 Purpose 划分加密域，避免不同模块密文交叉解密。
/// 密钥环目录由 Host 配置（AppPaths.KeyRingDir），SetApplicationName("PersonalMediaManager") 保证跨实例可恢复。
/// </remarks>
public sealed class DataProtectionFieldService : IProtectedFieldService
{
    public const string Purpose = "PmmField";

    private readonly IDataProtector _protector;

    public DataProtectionFieldService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
