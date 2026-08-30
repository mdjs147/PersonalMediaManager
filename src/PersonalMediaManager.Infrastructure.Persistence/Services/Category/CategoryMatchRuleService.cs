using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Category;
using PersonalMediaManager.Application.Services.Category;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Category;

/// <summary>分类匹配规则服务实现（Category_MatchRule CRUD + Conditions JSON 合法性校验）</summary>
/// <remarks>
/// 保存前用 JsonDocument.Parse 试解析 Conditions；失败抛 1000（错误码同其他校验失败）。
/// 不在服务层做更深层 Schema 校验（如 all/any/eq 谓词树合法性），保留到 D 阶段 ClassifyService 真实执行时处理。
/// CategoryId 存在性前置校验：避免 EF 抛 FK 异常被中间件统一吞为 9000。
/// </remarks>
internal sealed class CategoryMatchRuleService : ICategoryMatchRuleService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;

    public CategoryMatchRuleService(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<CategoryMatchRuleResponse>> ListAsync(long? categoryId, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<CategoryMatchRule> q = ctx.CategoryMatchRules.AsNoTracking();
        if (categoryId is not null)
            q = q.Where(r => r.CategoryId == categoryId.Value);

        List<CategoryMatchRule> rows = await q
            .OrderBy(r => r.CategoryId).ThenBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);
        return rows.Select(ToResponse).ToList();
    }

    public async Task<CategoryMatchRuleResponse> CreateAsync(CreateCategoryMatchRuleRequest req, CancellationToken ct = default)
    {
        string conditions = ValidateConditionsJson(req.Conditions);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (!await ctx.CategoryDefinitions.AnyAsync(c => c.Id == req.CategoryId, ct))
            throw new BusinessException($"分类 Id={req.CategoryId} 不存在");

        CategoryMatchRule entity = new()
        {
            CategoryId = req.CategoryId,
            Name = req.Name?.Trim(),
            Conditions = conditions,
            Priority = req.Priority,
            Enabled = req.Enabled,
        };
        ctx.CategoryMatchRules.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<CategoryMatchRuleResponse> UpdateAsync(UpdateCategoryMatchRuleRequest req, CancellationToken ct = default)
    {
        string conditions = ValidateConditionsJson(req.Conditions);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        CategoryMatchRule? entity = await ctx.CategoryMatchRules.FirstOrDefaultAsync(r => r.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"匹配规则 Id={req.Id} 不存在");

        // 乐观并发：客户端提交的 RowVersion 须与 DB 当前值一致
        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"匹配规则 Id={req.Id} 已被其他操作修改，请刷新后重试");

        if (entity.CategoryId != req.CategoryId
            && !await ctx.CategoryDefinitions.AnyAsync(c => c.Id == req.CategoryId, ct))
            throw new BusinessException($"分类 Id={req.CategoryId} 不存在");

        entity.CategoryId = req.CategoryId;
        entity.Name = req.Name?.Trim();
        entity.Conditions = conditions;
        entity.Priority = req.Priority;
        entity.Enabled = req.Enabled;
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteCategoryMatchRuleRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        CategoryMatchRule? entity = await ctx.CategoryMatchRules.FirstOrDefaultAsync(r => r.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"匹配规则 Id={req.Id} 不存在");

        ctx.CategoryMatchRules.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    private static CategoryMatchRuleResponse ToResponse(CategoryMatchRule r) =>
        new(r.Id, r.CategoryId, r.Name, r.Conditions, r.Priority, r.Enabled, r.RowVersion, r.CreatedAt, r.UpdatedAt);

    private static string ValidateConditionsJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new BusinessException("Conditions 不能为空（至少传 {}）");
        string trimmed = raw.Trim();
        if (trimmed.Length > 4000)
            throw new BusinessException("Conditions 长度不能超过 4000 字符");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new BusinessException("Conditions 必须是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new BusinessException($"Conditions 不是合法 JSON：{ex.Message}");
        }
        return trimmed;
    }
}
