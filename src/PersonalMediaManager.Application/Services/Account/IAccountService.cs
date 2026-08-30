using PersonalMediaManager.Application.Dtos.Account;

namespace PersonalMediaManager.Application.Services.Account;

/// <summary>账户管理服务契约（Admin 专用）</summary>
/// <remarks>
/// 实现放 Infrastructure.Persistence/Services/Account/AccountService。
/// 删除最后一个 Admin → BusinessException「不能删除最后一个管理员」。
/// </remarks>
public interface IAccountService
{
    Task<IReadOnlyList<UserListItem>> ListAsync(CancellationToken ct = default);

    Task<UserListItem> CreateAsync(CreateUserRequest req, CancellationToken ct = default);

    Task DeleteAsync(long userId, CancellationToken ct = default);

    Task ChangePasswordAsync(long userId, ChangePasswordRequest req, CancellationToken ct = default);
}
