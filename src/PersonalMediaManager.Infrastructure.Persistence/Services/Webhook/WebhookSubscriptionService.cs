using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Webhook;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

/// <summary>Webhook 订阅服务实现（CRUD + Secret 加密）</summary>
/// <remarks>
/// Name 唯一；URL 必须 http/https；Events 至少 1 项（避免无意义订阅）；TimeoutSeconds∈[1,60]。
/// Secret 三态 null=不变 / ""=清空 / 非空=Protect；响应仅暴露 HasSecret 布尔。
/// 删除时 Webhook_Delivery 由 DB FK CASCADE 自动清。
/// </remarks>
internal sealed class WebhookSubscriptionService : IWebhookSubscriptionService
{
    /// <summary>Webhook 总开关 key（须与 SystemSettingConfig 种子 + ArchiveService.WebhookEnabledKey 一致）</summary>
    public const string GlobalEnabledKey = "Webhook_Enabled";

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IProtectedFieldService _protector;
    private readonly IWebhookTester _tester;

    public WebhookSubscriptionService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IProtectedFieldService protector,
        IWebhookTester tester)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _tester = tester;
    }

    public async Task<IReadOnlyList<WebhookSubscriptionResponse>> ListAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        // 单查询 LEFT-correlated 子查询：每个订阅同步带出 Delivered / Failed 计数
        // （SQLite 翻成单 SELECT 含两个相关子查询，替代「2 次 round-trip + 内存 join」）
        var rows = await ctx.WebhookSubscriptions.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                Sub = s,
                Delivered = ctx.WebhookDeliveries.Count(d => d.SubscriptionId == s.Id && d.Status == WebhookDeliveryStatus.Success),
                Failed = ctx.WebhookDeliveries.Count(d => d.SubscriptionId == s.Id && d.Status == WebhookDeliveryStatus.Failed),
            })
            .ToListAsync(ct);

        return rows.Select(r => ToResponse(r.Sub, r.Delivered, r.Failed)).ToList();
    }

    public async Task<WebhookSubscriptionResponse> CreateAsync(CreateWebhookSubscriptionRequest req, CancellationToken ct = default)
    {
        // Name / Url 非空 + 长度、Url http(s) 格式、TimeoutSeconds 范围已由 DTO DataAnnotations 边界拦截；
        // 此处仅做归一化 Trim + Events 集合 / 逐元素校验（集合元素级，无法声明式表达，KEEP）。
        string name = req.Name.Trim();
        string url = req.Url.Trim();
        List<string> events = NormalizeEvents(req.Events);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        if (await ctx.WebhookSubscriptions.AnyAsync(s => s.Name == name, ct))
            throw new BusinessException($"订阅 {name} 已存在");

        WebhookSubscription entity = new()
        {
            Name = name,
            Url = url,
            SecretEncrypted = string.IsNullOrEmpty(req.Secret) ? null : _protector.Protect(req.Secret),
            Events = events,
            Enabled = req.Enabled,
            TimeoutSeconds = req.TimeoutSeconds,
        };
        ctx.WebhookSubscriptions.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<WebhookSubscriptionResponse> UpdateAsync(UpdateWebhookSubscriptionRequest req, CancellationToken ct = default)
    {
        // 同 CreateAsync：字段级无状态校验已迁至 DTO DataAnnotations；此处只归一化 + Events 集合校验。
        string name = req.Name.Trim();
        string url = req.Url.Trim();
        List<string> events = NormalizeEvents(req.Events);

        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WebhookSubscription? entity = await ctx.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"订阅 Id={req.Id} 不存在");

        if (entity.Name != name
            && await ctx.WebhookSubscriptions.AnyAsync(s => s.Id != req.Id && s.Name == name, ct))
            throw new BusinessException($"订阅 {name} 已存在");

        entity.Name = name;
        entity.Url = url;
        if (req.Secret is not null)
            entity.SecretEncrypted = req.Secret.Length == 0 ? null : _protector.Protect(req.Secret);
        entity.Events = events;
        entity.Enabled = req.Enabled;
        entity.TimeoutSeconds = req.TimeoutSeconds;
        await ctx.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(DeleteWebhookSubscriptionRequest req, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WebhookSubscription? entity = await ctx.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == req.Id, ct);
        if (entity is null)
            throw new BusinessException($"订阅 Id={req.Id} 不存在");

        ctx.WebhookSubscriptions.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<WebhookTestResult> TestAsync(long id, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        WebhookSubscription? entity = await ctx.WebhookSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null)
            throw new BusinessException($"订阅 Id={id} 不存在");

        string? plainSecret = null;
        if (!string.IsNullOrEmpty(entity.SecretEncrypted))
        {
            try
            {
                plainSecret = _protector.Unprotect(entity.SecretEncrypted);
            }
            catch (Exception ex)
            {
                throw new BusinessException($"Secret 解密失败：{ex.Message}");
            }
        }

        return await _tester.SendAsync(entity.Url, plainSecret, entity.TimeoutSeconds, ct);
    }

    public async Task<bool> GetGlobalEnabledAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        string? raw = await ctx.SystemSettings.AsNoTracking()
            .Where(s => s.Key == GlobalEnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return ParseEnabled(raw);
    }

    public async Task SetGlobalEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        SystemSetting? row = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Key == GlobalEnabledKey, ct);
        if (row is null)
        {
            // 旧库未种子时兜底 upsert（与 UpdateSettingService.EnsureRow 同款，保护 db 升级期）
            ctx.SystemSettings.Add(new SystemSetting
            {
                Key = GlobalEnabledKey,
                Value = enabled ? "true" : "false",
                Category = "Webhook",
                Description = "Webhook 总开关（false=关闭时归档不产生投递记录）",
            });
        }
        else
        {
            row.Value = enabled ? "true" : "false";
        }
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>解析总开关（默认 false：缺失 / 非 "true" 一律关闭）</summary>
    private static bool ParseEnabled(string? raw) =>
        string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static WebhookSubscriptionResponse ToResponse(WebhookSubscription s, int delivered = 0, int failed = 0) =>
        new(s.Id, s.Name, s.Url, !string.IsNullOrEmpty(s.SecretEncrypted),
            s.Events.ToArray(), s.Enabled, s.TimeoutSeconds, delivered, failed,
            s.CreatedAt, s.UpdatedAt);

    // Events 的「非空 / 至少 1 项」已上移 DTO（[Required]+[MinLength(1)]）；此处仅保留无法声明式表达的逐元素校验（空名 / 超长）+ Trim + 去重
    private static List<string> NormalizeEvents(IReadOnlyList<string> events)
    {
        List<string> normalized = new(events.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string e in events)
        {
            if (string.IsNullOrWhiteSpace(e))
                throw new BusinessException("Event 名不能为空");
            string t = e.Trim();
            if (t.Length > 64)
                throw new BusinessException("Event 名长度不能超过 64 字符");
            if (!WebhookEvents.All.Contains(t))
                throw new BusinessException($"events 包含未知事件类型：{t}");
            if (seen.Add(t)) normalized.Add(t);
        }
        return normalized;
    }
}
