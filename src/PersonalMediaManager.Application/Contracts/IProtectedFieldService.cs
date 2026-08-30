namespace PersonalMediaManager.Application.Contracts;

/// <summary>敏感字段加解密契约</summary>
/// <remarks>
/// 实现在 Infrastructure.Platform 包装 ASP.NET Core DataProtection；密钥环存本地用户目录（需求文档 §4.3）。
/// 应用场景：TMDB ApiKey / AI Provider ApiKey / Webhook Secret 写入数据库前 Protect、读出时 Unprotect。
/// </remarks>
public interface IProtectedFieldService
{
    /// <summary>加密：明文 → base64 密文</summary>
    string Protect(string plaintext);

    /// <summary>解密：base64 密文 → 明文</summary>
    string Unprotect(string ciphertext);
}
