using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>解析规则服务实现（Parse_Rule CRUD + 即席 Regex 测试）</summary>
/// <remarks>
/// 保存前用 Regex(Compiled, Timeout=500ms) 试编译，编译失败抛 1000（错误信息包含原因）。
/// DefaultType 仅允许 movie / tv / null；ConfidenceBonus 限制在 [0, 0.3]（数据库设计与需求文档约束）。
/// /test 不读 DB，纯内存 Regex.Match；为防止恶意正则把测试端点拖死，复用同样的 500ms Timeout。
/// </remarks>
internal sealed class ParseRuleService : IParseRuleService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly HashSet<string> AllowedDefaultTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "tv",
    };

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;

    public ParseRuleService(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ParseRuleResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        List<ParseRule> rows = await ctx.ParseRules.AsNoTracking()
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);
        return rows.Select(ToResponse).ToList();
    }

    public async Task<ParseRuleResponse> CreateAsync(CreateParseRuleRequest req, CancellationToken ct = default)
    {
        string name = NormalizeName(req.Name);
        string pattern = ValidatePattern(req.Pattern);
        string? defaultType = NormalizeDefaultType(req.DefaultType);
        ValidatePriority(req.Priority);
        ValidateConfidenceBonus(req.ConfidenceBonus);
        ValidateDescription(req.Description);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.ParseRules.AnyAsync(r => r.Name == name, ct))
            throw new BusinessException($"解析规则 {name} 已存在");

        ParseRule entity = new()
        {
            Name = name,
            Scope = req.Scope,
            Pattern = pattern,
            DefaultType = defaultType,
            ForceType = req.ForceType,
            Priority = req.Priority,
            ConfidenceBonus = req.ConfidenceBonus,
            Enabled = req.Enabled,
            Description = req.Description?.Trim(),
        };
        ctx.ParseRules.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<ParseRuleResponse> UpdateAsync(UpdateParseRuleRequest req, CancellationToken ct = default)
    {
        string name = NormalizeName(req.Name);
        string pattern = ValidatePattern(req.Pattern);
        string? defaultType = NormalizeDefaultType(req.DefaultType);
        ValidatePriority(req.Priority);
        ValidateConfidenceBonus(req.ConfidenceBonus);
        ValidateDescription(req.Description);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseRule? entity = await ctx.ParseRules.FirstOrDefaultAsync(r => r.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"解析规则 Id={req.Id} 不存在");

        if (entity.Name != name
            && await ctx.ParseRules.AnyAsync(r => r.Id != req.Id && r.Name == name, ct))
            throw new BusinessException($"解析规则 {name} 已存在");

        // 乐观并发：客户端提交的 RowVersion 须与 DB 当前值一致；不一致说明客户端持有旧版本，提前抛 1000 避免覆盖更新的数据
        if (entity.RowVersion != req.RowVersion)
            throw new BusinessException($"解析规则 Id={req.Id} 已被其他操作修改，请刷新后重试");

        entity.Name = name;
        entity.Scope = req.Scope;
        entity.Pattern = pattern;
        entity.DefaultType = defaultType;
        entity.ForceType = req.ForceType;
        entity.Priority = req.Priority;
        entity.ConfidenceBonus = req.ConfidenceBonus;
        entity.Enabled = req.Enabled;
        entity.Description = req.Description?.Trim();
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteParseRuleRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        ParseRule? entity = await ctx.ParseRules.FirstOrDefaultAsync(r => r.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"解析规则 Id={req.Id} 不存在");

        ctx.ParseRules.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public Task<TestParseRuleResponse> TestAsync(TestParseRuleRequest req, CancellationToken ct = default)
    {
        // r2 P2-r2.7：签名异步化便于上游接收 CancellationToken；Regex 本身是 CPU 同步 + 500ms Timeout 守护，
        // 在进入匹配前主动 ThrowIfCancellationRequested 让取消信号在请求级别被尊重
        ct.ThrowIfCancellationRequested();
        if (req is null || string.IsNullOrWhiteSpace(req.Pattern))
            throw new BusinessException("Pattern 不能为空");
        if (req.Sample is null)
            throw new BusinessException("Sample 不能为 null");

        Regex regex;
        try
        {
            regex = new Regex(req.Pattern, RegexOptions.Compiled, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new TestParseRuleResponse(false, new Dictionary<string, string>(), 0, $"正则编译失败：{ex.Message}"));
        }

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            Match match = regex.Match(req.Sample);
            sw.Stop();
            if (!match.Success)
                return Task.FromResult(new TestParseRuleResponse(false, new Dictionary<string, string>(), sw.Elapsed.TotalMilliseconds, null));

            Dictionary<string, string> groups = new(StringComparer.Ordinal);
            foreach (string groupName in regex.GetGroupNames())
            {
                // 跳过数字组名（仅返回命名捕获）
                if (int.TryParse(groupName, out _)) continue;
                Group g = match.Groups[groupName];
                if (g.Success)
                    groups[groupName] = g.Value;
            }
            return Task.FromResult(new TestParseRuleResponse(true, groups, sw.Elapsed.TotalMilliseconds, null));
        }
        catch (RegexMatchTimeoutException)
        {
            sw.Stop();
            return Task.FromResult(new TestParseRuleResponse(false, new Dictionary<string, string>(), sw.Elapsed.TotalMilliseconds, "正则匹配超过 500ms 超时"));
        }
    }

    public async Task<ImportParseRulesResult> ImportAsync(Stream input, ParseRuleImportMode mode, CancellationToken ct = default)
    {
        if (input is null) throw new BusinessException("未上传文件");

        // JSON 反序列化（PascalCase / camelCase 都接受；ParseScope 走全局 JsonStringEnumConverter）
        List<ImportParseRuleItem>? items;
        try
        {
            JsonSerializerOptions opts = new(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            };
            items = await JsonSerializer.DeserializeAsync<List<ImportParseRuleItem>>(input, opts, ct);
        }
        catch (JsonException ex)
        {
            throw new BusinessException($"JSON 解析失败：{ex.Message}");
        }

        if (items is null || items.Count == 0)
            throw new BusinessException("导入文件为空或不是规则数组");
        if (items.Count > 500)
            throw new BusinessException("单次导入数量不能超过 500 条");

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);

        int deletedBefore = 0;
        if (mode == ParseRuleImportMode.Replace)
        {
            deletedBefore = await ctx.ParseRules.ExecuteDeleteAsync(ct);
        }

        // Replace 模式下已清空，按 Name 重名只可能在导入文件内部出现；Merge 模式下需查现有
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        if (mode == ParseRuleImportMode.Merge)
        {
            List<string> names = await ctx.ParseRules.AsNoTracking().Select(r => r.Name).ToListAsync(ct);
            foreach (string n in names) existing.Add(n);
        }

        int added = 0;
        int skipped = 0;
        List<ImportParseRuleFailure> failures = new();
        for (int i = 0; i < items.Count; i++)
        {
            ImportParseRuleItem item = items[i];
            try
            {
                string name = NormalizeName(item.Name);
                string pattern = ValidatePattern(item.Pattern);
                string? defaultType = NormalizeDefaultType(item.DefaultType);
                ValidatePriority(item.Priority);
                ValidateConfidenceBonus(item.ConfidenceBonus);
                ValidateDescription(item.Description);

                if (existing.Contains(name))
                {
                    skipped++;
                    continue;
                }

                ctx.ParseRules.Add(new ParseRule
                {
                    Name = name,
                    Scope = item.Scope,
                    Pattern = pattern,
                    DefaultType = defaultType,
                    ForceType = item.ForceType,
                    Priority = item.Priority,
                    ConfidenceBonus = item.ConfidenceBonus,
                    Enabled = item.Enabled,
                    Description = item.Description?.Trim(),
                });
                existing.Add(name);
                added++;
            }
            catch (BusinessException ex)
            {
                failures.Add(new ImportParseRuleFailure(i, item?.Name, ex.Message));
            }
        }

        if (added > 0) await ctx.SaveChangesAsync(ct);
        return new ImportParseRulesResult(added, skipped, deletedBefore, failures);
    }

    public IReadOnlyList<BuiltinParseRuleResponse> ListBuiltins() => BuiltinRulesCatalog.All;

    private static ParseRuleResponse ToResponse(ParseRule r) =>
        new(r.Id, r.Name, r.Scope, r.Pattern, r.DefaultType, r.ForceType, r.Priority, r.ConfidenceBonus, r.Enabled, r.Description, r.RowVersion, r.CreatedAt, r.UpdatedAt);

    private static string NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new BusinessException("规则 Name 不能为空");
        string trimmed = raw.Trim();
        if (trimmed.Length > 100)
            throw new BusinessException("规则 Name 长度不能超过 100 字符");
        return trimmed;
    }

    private static string ValidatePattern(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            throw new BusinessException("Pattern 不能为空");
        if (raw.Length > 1000)
            throw new BusinessException("Pattern 长度不能超过 1000 字符");

        try
        {
            // 试编译：Compiled + 500ms Timeout，与执行时一致，把恶意 / 错误正则拦在保存前
            _ = new Regex(raw, RegexOptions.Compiled, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new BusinessException($"正则编译失败：{ex.Message}");
        }
        return raw;
    }

    private static string? NormalizeDefaultType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim().ToLowerInvariant();
        if (!AllowedDefaultTypes.Contains(trimmed))
            throw new BusinessException("DefaultType 仅允许 movie / tv 或 null");
        return trimmed;
    }

    private static void ValidatePriority(int priority)
    {
        if (priority < 0 || priority > 9999)
            throw new BusinessException("Priority 必须在 [0, 9999] 范围内");
    }

    private static void ValidateConfidenceBonus(double bonus)
    {
        if (bonus < 0 || bonus > 0.3)
            throw new BusinessException("ConfidenceBonus 必须在 [0, 0.3] 范围内");
    }

    private static void ValidateDescription(string? description)
    {
        if (description is not null && description.Length > 500)
            throw new BusinessException("Description 长度不能超过 500 字符");
    }
}
