using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Application.Services.Settings;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

/// <summary>媒体扩展名服务（Singleton）— CRUD + 内存缓存提供方</summary>
/// <remarks>
/// 同时实现 IMediaExtensionService（CRUD 端点使用）与 IMediaExtensionProvider（FileIntakeService / ScanService 高频路径使用）。
/// 注册为 Singleton：内存缓存 _cache 跨请求共享；CRUD 写操作完成后调 RefreshAsync 更新缓存；
/// 启动时由 IHostedService（MediaExtensionCacheInitializer）预热；启动阶段缓存未就绪时
/// GetEnabledExtensions() 降级返回 MediaFileExtensions.Video（编译期内置白名单），保证监控正常运行。
/// </remarks>
internal sealed class MediaExtensionService : IMediaExtensionService, IMediaExtensionProvider
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;

    // volatile 保证多线程可见性（无需 lock：HashSet 只读，整体替换）
    private volatile IReadOnlySet<string>? _cache;

    public MediaExtensionService(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    // ─── IMediaExtensionProvider ────────────────────────────────────────────

    public IReadOnlySet<string> GetEnabledExtensions() =>
        _cache ?? MediaFileExtensions.Video;

    public bool IsVideo(string fileNameOrPath) =>
        !string.IsNullOrEmpty(fileNameOrPath) &&
        GetEnabledExtensions().Contains(Path.GetExtension(fileNameOrPath));

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        List<string> exts = await ctx.SystemMediaExtensions.AsNoTracking()
            .Where(e => e.Enabled)
            .Select(e => e.Extension)
            .ToListAsync(ct);
        _cache = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
    }

    // ─── IMediaExtensionService ─────────────────────────────────────────────

    public async Task<IReadOnlyList<MediaExtensionResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        List<SystemMediaExtension> rows = await ctx.SystemMediaExtensions.AsNoTracking()
            .OrderBy(e => e.Extension)
            .ToListAsync(ct);
        return rows.Select(ToResponse).ToList();
    }

    public async Task<MediaExtensionResponse> CreateAsync(CreateMediaExtensionRequest req, CancellationToken ct = default)
    {
        string ext = NormalizeExtension(req.Extension);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.SystemMediaExtensions.AnyAsync(e => e.Extension == ext, ct))
            throw new BusinessException($"扩展名已存在：{ext}");

        SystemMediaExtension entity = new()
        {
            Extension = ext,
            Description = req.Description?.Trim(),
            Enabled = req.Enabled,
        };
        ctx.SystemMediaExtensions.Add(entity);
        await ctx.SaveChangesAsync(ct);
        await RefreshAsync(ct);
        return ToResponse(entity);
    }

    public async Task<MediaExtensionResponse> UpdateAsync(UpdateMediaExtensionRequest req, CancellationToken ct = default)
    {
        string ext = NormalizeExtension(req.Extension);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        SystemMediaExtension? entity = await ctx.SystemMediaExtensions.FirstOrDefaultAsync(e => e.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"扩展名 Id={req.Id} 不存在");

        if (entity.Extension != ext && await ctx.SystemMediaExtensions.AnyAsync(e => e.Id != req.Id && e.Extension == ext, ct))
            throw new BusinessException($"扩展名已存在：{ext}");

        entity.Extension = ext;
        entity.Description = req.Description?.Trim();
        entity.Enabled = req.Enabled;
        await ctx.SaveChangesAsync(ct);
        await RefreshAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteMediaExtensionRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        SystemMediaExtension? entity = await ctx.SystemMediaExtensions.FirstOrDefaultAsync(e => e.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"扩展名 Id={req.Id} 不存在");

        ctx.SystemMediaExtensions.Remove(entity);
        await ctx.SaveChangesAsync(ct);
        await RefreshAsync(ct);
    }

    // ─── 私有工具 ───────────────────────────────────────────────────────────

    private static MediaExtensionResponse ToResponse(SystemMediaExtension e) =>
        new(e.Id, e.Extension, e.Description, e.Enabled, e.CreatedAt, e.UpdatedAt);

    // 空 / 格式 / 长度校验已迁至 DTO DataAnnotations；此处仅做首尾空白归一 + 小写折叠（入库统一小写）
    private static string NormalizeExtension(string raw) => raw.Trim().ToLowerInvariant();
}
