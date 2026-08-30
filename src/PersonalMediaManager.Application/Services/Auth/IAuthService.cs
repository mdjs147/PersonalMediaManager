using PersonalMediaManager.Application.Dtos.Auth;

namespace PersonalMediaManager.Application.Services.Auth;

/// <summary>认证服务契约</summary>
/// <remarks>
/// 实现放 Infrastructure.Persistence/Services/Auth/AuthService。
/// 登录失败不锁定（需求文档 §3.12），仅记 Audit_Operation Action="Auth.LoginFailed"。
/// </remarks>
public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest req, string? ip, CancellationToken ct = default);

    Task LogoutAsync(long userId, string? ip, CancellationToken ct = default);

    Task<UserSummary> GetMeAsync(long userId, CancellationToken ct = default);
}
