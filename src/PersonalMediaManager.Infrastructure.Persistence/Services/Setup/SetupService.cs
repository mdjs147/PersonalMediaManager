using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Setup;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

/// <summary>初始化向导服务实现（需求文档 §3.12 首次向导）</summary>
/// <remarks>
/// Setup 完成状态存 System_Setting Key="System.SetupCompleted"；CreateAdmin 的用户名非空 / 密码 ≥6（§3.12）等
/// A 类字段校验已上移至 CreateAdminRequest DataAnnotations，由 [ApiController] 边界拦截；
/// Complete 要求已有 Admin。所有写操作走 IDbContextFactory 创建独立 ctx（响应 CLAUDE.md §八）。
/// </remarks>
internal sealed class SetupService : ISetupService
{
    public const string SetupCompletedKey = "System.SetupCompleted";

    /// <summary>分类目标根目录占位符，须与 DataSeeder.UnsetRoot 一致；用于统计「未配置落点」的分类数</summary>
    private const string CategoryUnsetRoot = "<UNSET>";

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IPasswordHasher _hasher;

    public SetupService(IDbContextFactory<PmmDbContext> dbFactory, IPasswordHasher hasher)
    {
        _dbFactory = dbFactory;
        _hasher = hasher;
    }

    public async Task<SetupStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        bool isCompleted = await ReadCompletedFlagAsync(ctx, ct);
        bool hasAdmin = await ctx.UserAccounts.AnyAsync(u => u.Role == UserRole.Admin, ct);
        return new SetupStatusResponse(isCompleted, hasAdmin);
    }

    public async Task<CreateAdminResponse> CreateAdminAsync(CreateAdminRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        // 安全守门（纵深防御，不依赖 SetupGuardMiddleware）：已存在 Admin 即视为初始化向导已完成，
        // 拒绝再次创建管理员——否则 setup 完成后该匿名端点会沦为「匿名 → 创建 Admin」的认证绕过后门。
        if (await ctx.UserAccounts.AnyAsync(u => u.Role == UserRole.Admin, ct))
            throw new BusinessException("管理员已存在，初始化向导已完成");

        if (await ctx.UserAccounts.AnyAsync(u => u.Username == req.Username, ct))
            throw new BusinessException("用户名已存在");

        UserAccount admin = new()
        {
            Username = req.Username,
            PasswordHash = _hasher.Hash(req.Password),
            Role = UserRole.Admin,
        };
        ctx.UserAccounts.Add(admin);
        await ctx.SaveChangesAsync(ct);

        return new CreateAdminResponse(admin.Id, admin.Username);
    }

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        if (!await ctx.UserAccounts.AnyAsync(u => u.Role == UserRole.Admin, ct))
            throw new BusinessException("请先创建管理员账号");

        SystemSetting? marker = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Key == SetupCompletedKey, ct);
        if (marker is null)
        {
            ctx.SystemSettings.Add(new SystemSetting
            {
                Key = SetupCompletedKey,
                Value = "true",
                Category = "System",
                Description = "初始化向导完成标记",
            });
        }
        else
        {
            marker.Value = "true";
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> IsCompletedAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        return await ReadCompletedFlagAsync(ctx, ct);
    }

    public async Task<SetupChecklistResponse> GetChecklistAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        // 同一 DbContext 顺序查（CLAUDE.md §八：禁止同 ctx 上并发多个 EF 查询）
        bool isCompleted = await ReadCompletedFlagAsync(ctx, ct);
        bool hasAdmin = await ctx.UserAccounts.AnyAsync(u => u.Role == UserRole.Admin, ct);
        int watchFolderCount = await ctx.WatchFolders.CountAsync(ct);
        bool tmdbHasApiKey = await ctx.TmdbSettings
            .AnyAsync(t => t.ApiKeyEncrypted != null && t.ApiKeyEncrypted != "", ct);
        int categoryTotal = await ctx.CategoryDefinitions.CountAsync(ct);
        int categoryUnset = await ctx.CategoryDefinitions.CountAsync(c => c.TargetRoot == CategoryUnsetRoot, ct);
        int aiProviderCount = await ctx.ParseAiProviders.CountAsync(ct);

        return new SetupChecklistResponse(
            isCompleted, hasAdmin, watchFolderCount, tmdbHasApiKey, categoryTotal, categoryUnset, aiProviderCount);
    }

    private static async Task<bool> ReadCompletedFlagAsync(PmmDbContext ctx, CancellationToken ct)
    {
        string? value = await ctx.SystemSettings
            .Where(s => s.Key == SetupCompletedKey).Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return value == "true";
    }
}
