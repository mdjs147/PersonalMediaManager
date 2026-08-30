using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Watch;
using PersonalMediaManager.Application.Services.Watch;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Watch;

/// <summary>忽略规则服务实现（Watch_IgnoreRule CRUD）</summary>
/// <remarks>
/// Extension 规则：Pattern 必须以 '.' 开头并归一化为小写（数据库设计 §1.4 约定）；
/// Keyword 规则：Trim 后存储，小写归一化（匹配阶段亦做小写比较）；
/// 同 Type + Pattern 唯一（UQ_Watch_IgnoreRule_Type_Pattern 兜底，服务层提前校验）。
/// </remarks>
internal sealed class WatchIgnoreRuleService : IWatchIgnoreRuleService
{
    private static readonly char[] InvalidExtensionChars = [' ', '\t', '\\', '/'];

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;

    public WatchIgnoreRuleService(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<WatchIgnoreRuleResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        List<WatchIgnoreRule> rows = await ctx.WatchIgnoreRules.AsNoTracking()
            .OrderBy(r => r.Type).ThenBy(r => r.Pattern)
            .ToListAsync(ct);
        return rows.Select(ToResponse).ToList();
    }

    public async Task<WatchIgnoreRuleResponse> CreateAsync(CreateWatchIgnoreRuleRequest req, CancellationToken ct = default)
    {
        string pattern = NormalizePattern(req.Type, req.Pattern);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.WatchIgnoreRules.AnyAsync(r => r.Type == req.Type && r.Pattern == pattern, ct))
            throw new BusinessException($"忽略规则已存在：{req.Type} / {pattern}");

        WatchIgnoreRule entity = new()
        {
            Type = req.Type,
            Pattern = pattern,
            Description = req.Description?.Trim(),
            Enabled = req.Enabled,
        };
        ctx.WatchIgnoreRules.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<WatchIgnoreRuleResponse> UpdateAsync(UpdateWatchIgnoreRuleRequest req, CancellationToken ct = default)
    {
        string pattern = NormalizePattern(req.Type, req.Pattern);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WatchIgnoreRule? entity = await ctx.WatchIgnoreRules.FirstOrDefaultAsync(r => r.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"忽略规则 Id={req.Id} 不存在");

        if ((entity.Type != req.Type || entity.Pattern != pattern)
            && await ctx.WatchIgnoreRules.AnyAsync(r => r.Id != req.Id && r.Type == req.Type && r.Pattern == pattern, ct))
            throw new BusinessException($"忽略规则已存在：{req.Type} / {pattern}");

        entity.Type = req.Type;
        entity.Pattern = pattern;
        entity.Description = req.Description?.Trim();
        entity.Enabled = req.Enabled;
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteWatchIgnoreRuleRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WatchIgnoreRule? entity = await ctx.WatchIgnoreRules.FirstOrDefaultAsync(r => r.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"忽略规则 Id={req.Id} 不存在");

        ctx.WatchIgnoreRules.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    private static WatchIgnoreRuleResponse ToResponse(WatchIgnoreRule r) =>
        new(r.Id, r.Type, r.Pattern, r.Description, r.Enabled, r.CreatedAt, r.UpdatedAt);

    // Pattern 非空 / 长度（≤200）已由 DTO 上的 DataAnnotations 在模型绑定阶段拦截（档 2 边界校验）；
    // 此处仅保留「依赖 Type==Extension」的跨字段规则 + 小写归一化（无法表达为单字段声明式校验，故 KEEP）。
    private static string NormalizePattern(IgnoreRuleType type, string raw)
    {
        string trimmed = raw.Trim().ToLowerInvariant();

        if (type == IgnoreRuleType.Extension)
        {
            if (!trimmed.StartsWith('.'))
                throw new BusinessException("Extension 类型的 Pattern 必须以 '.' 开头");
            if (trimmed.Length < 2)
                throw new BusinessException("Extension 类型的 Pattern 至少 2 个字符（含点）");
            if (trimmed.IndexOfAny(InvalidExtensionChars, 1) >= 0)
                throw new BusinessException("Extension 类型的 Pattern 不能包含空白或路径分隔符");
        }
        return trimmed;
    }
}
