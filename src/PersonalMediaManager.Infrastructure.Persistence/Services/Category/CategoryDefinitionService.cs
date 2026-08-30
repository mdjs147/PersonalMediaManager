using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Category;
using PersonalMediaManager.Application.Services.Category;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Category;

/// <summary>分类定义服务实现（Category_Definition CRUD + 删除前引用检查）</summary>
/// <remarks>
/// Name 唯一：服务查 DB 前置校验 + DB UQ 兜底（无状态的空 / 长度 / 范围字段校验已迁至 Request DTO 的
/// DataAnnotations，由 [ApiController] 在模型绑定阶段统一拦截）；TargetRoot 不做目录存在性检查
/// （部署初期目标根目录可能尚未挂载，且 D 阶段 Archive 时会有专门的归档前置校验）。
/// 删除前扫描 Media_Item.CategoryId 引用：任意一条引用即抛 1000（不区分状态，避免历史记录被悬挂）。
/// </remarks>
internal sealed class CategoryDefinitionService : ICategoryDefinitionService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;

    public CategoryDefinitionService(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<CategoryDefinitionResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        // 单查询 + 相关子查询带出 MediaItemCount（无终态过滤：含 Completed/Skipped/Ignored/Cancelled/Failed 全量计数，
        // 与 DashboardPlus donut「分类持有的所有媒体记录」语义一致）
        var rows = await ctx.CategoryDefinitions.AsNoTracking()
            .OrderBy(c => c.Priority).ThenBy(c => c.Id)
            .Select(c => new
            {
                Cat = c,
                MediaItemCount = ctx.MediaItems.Count(m => m.CategoryId == c.Id),
            })
            .ToListAsync(ct);
        return rows.Select(r => ToResponse(r.Cat, r.MediaItemCount)).ToList();
    }

    public async Task<CategoryDefinitionResponse> CreateAsync(CreateCategoryDefinitionRequest req, CancellationToken ct = default)
    {
        string name = NormalizeName(req.Name);
        string targetRoot = NormalizeTargetRoot(req.TargetRoot);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.CategoryDefinitions.AnyAsync(c => c.Name == name, ct))
            throw new BusinessException($"分类 {name} 已存在");

        CategoryDefinition entity = new()
        {
            Name = name,
            MediaType = req.MediaType,
            TargetRoot = targetRoot,
            Priority = req.Priority,
            Description = req.Description?.Trim(),
        };
        ctx.CategoryDefinitions.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<CategoryDefinitionResponse> UpdateAsync(UpdateCategoryDefinitionRequest req, CancellationToken ct = default)
    {
        string name = NormalizeName(req.Name);
        string targetRoot = NormalizeTargetRoot(req.TargetRoot);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        CategoryDefinition? entity = await ctx.CategoryDefinitions.FirstOrDefaultAsync(c => c.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"分类 Id={req.Id} 不存在");

        if (entity.Name != name
            && await ctx.CategoryDefinitions.AnyAsync(c => c.Id != req.Id && c.Name == name, ct))
            throw new BusinessException($"分类 {name} 已存在");

        // 乐观并发：客户端提交的 RowVersion 须与 DB 当前值一致；
        // 不一致说明客户端持有旧版本，提前抛 1000 避免覆盖更新的数据
        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"分类 Id={req.Id} 已被其他操作修改，请刷新后重试");

        entity.Name = name;
        entity.MediaType = req.MediaType;
        entity.TargetRoot = targetRoot;
        entity.Priority = req.Priority;
        entity.Description = req.Description?.Trim();
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteCategoryDefinitionRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        CategoryDefinition? entity = await ctx.CategoryDefinitions.FirstOrDefaultAsync(c => c.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"分类 Id={req.Id} 不存在");

        bool hasReference = await ctx.MediaItems.AsNoTracking()
            .AnyAsync(m => m.CategoryId == req.Id, ct);
        if (hasReference)
            throw new BusinessException($"分类 {entity.Name} 仍被 Media_Item 引用，无法删除");

        ctx.CategoryDefinitions.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    private static CategoryDefinitionResponse ToResponse(CategoryDefinition c, int mediaItemCount = 0) =>
        new(c.Id, c.Name, c.MediaType, c.TargetRoot, c.Priority, c.Description, mediaItemCount, c.RowVersion, c.CreatedAt, c.UpdatedAt);

    // 空 / 长度校验已迁至 DTO DataAnnotations；此处仅做首尾空白归一
    private static string NormalizeName(string raw) => raw.Trim();

    private static string NormalizeTargetRoot(string raw) => raw.Trim();
}
