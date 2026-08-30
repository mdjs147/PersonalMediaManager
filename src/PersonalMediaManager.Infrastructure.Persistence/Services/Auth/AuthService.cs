using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Auth;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Auth;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Auth;

/// <summary>认证服务实现：login / logout / me</summary>
/// <remarks>
/// 登录失败不锁定（需求文档 §3.12）；失败记 Audit Action="Auth.LoginFailed"（含 IP / 用户名），成功记 Action="Auth.Login"。
/// Logout 服务端无状态（JWT 不入库）；仅写一条 Audit。
/// </remarks>
internal sealed class AuthService : IAuthService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _tokenService;
    private readonly IAuditOperationWriter _audit;
    private readonly IClock _clock;

    public AuthService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IPasswordHasher hasher,
        IJwtTokenService tokenService,
        IAuditOperationWriter audit,
        IClock clock)
    {
        _dbFactory = dbFactory;
        _hasher = hasher;
        _tokenService = tokenService;
        _audit = audit;
        _clock = clock;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest req, string? ip, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        UserAccount? user = await ctx.UserAccounts.FirstOrDefaultAsync(u => u.Username == req.Username, ct);

        if (user is null || !_hasher.Verify(req.Password, user.PasswordHash))
        {
            await _audit.WriteAsync(
                userId: user?.Id,
                username: req.Username,
                action: "Auth.LoginFailed",
                ip: ip,
                ct: ct);
            throw new BusinessException("用户名或密码错误");
        }

        user.LastLoginAt = _clock.UtcNow;
        user.LastLoginIp = ip;
        await ctx.SaveChangesAsync(ct);

        string token = _tokenService.Generate(user.Id, user.Username, user.Role);

        await _audit.WriteAsync(
            userId: user.Id,
            username: user.Username,
            action: "Auth.Login",
            ip: ip,
            ct: ct);

        return new LoginResponse(token, new UserSummary(user.Id, user.Username, user.Role, user.LastLoginAt));
    }

    public async Task LogoutAsync(long userId, string? ip, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        string? username = await ctx.UserAccounts.Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(ct);
        await _audit.WriteAsync(userId, username, "Auth.Logout", ip: ip, ct: ct);
    }

    public async Task<UserSummary> GetMeAsync(long userId, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        UserAccount? user = await ctx.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            throw new BusinessException("用户不存在");
        return new UserSummary(user.Id, user.Username, user.Role, user.LastLoginAt);
    }
}
