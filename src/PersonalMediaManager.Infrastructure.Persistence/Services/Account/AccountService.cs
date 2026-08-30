using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Account;
using PersonalMediaManager.Application.Services.Account;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Account;

/// <summary>账户管理服务实现（Admin 专用，B6.3）</summary>
/// <remarks>
/// 列表 / 创建 / 删除 / 改密；删最后一个 Admin → BusinessException「不能删除最后一个管理员」。
/// 改密：验证旧密码（错则抛 BusinessException）；用户名非空 / 密码长度（≥6）等 A 类字段校验已上移至请求 DTO DataAnnotations，由 [ApiController] 边界拦截。
/// </remarks>
internal sealed class AccountService : IAccountService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPasswordHasher _hasher;

    public AccountService(IDbContextFactory<PmmDbContext> dbFactory, IPasswordHasher hasher)
    {
        _dbFactory = dbFactory;
        _hasher = hasher;
    }

    public async Task<IReadOnlyList<UserListItem>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        return await ctx.UserAccounts.AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new UserListItem(u.Id, u.Username, u.Role, u.LastLoginAt, u.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<UserListItem> CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        if (await ctx.UserAccounts.AnyAsync(u => u.Username == req.Username, ct))
            throw new BusinessException("用户名已存在");

        UserAccount user = new()
        {
            Username = req.Username,
            PasswordHash = _hasher.Hash(req.Password),
            Role = req.Role,
        };
        ctx.UserAccounts.Add(user);
        await ctx.SaveChangesAsync(ct);

        return new UserListItem(user.Id, user.Username, user.Role, user.LastLoginAt, user.CreatedAt);
    }

    public async Task DeleteAsync(long userId, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        UserAccount? user = await ctx.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            throw new BusinessException("用户不存在");

        if (user.Role == UserRole.Admin)
        {
            int adminCount = await ctx.UserAccounts.CountAsync(u => u.Role == UserRole.Admin, ct);
            if (adminCount <= 1)
                throw new BusinessException("不能删除最后一个管理员");
        }

        ctx.UserAccounts.Remove(user);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(long userId, ChangePasswordRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        UserAccount? user = await ctx.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            throw new BusinessException("用户不存在");
        if (!_hasher.Verify(req.OldPassword, user.PasswordHash))
            throw new BusinessException("旧密码错误");

        user.PasswordHash = _hasher.Hash(req.NewPassword);
        await ctx.SaveChangesAsync(ct);
    }
}
