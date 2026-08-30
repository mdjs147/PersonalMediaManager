using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Maintenance;

/// <summary>系统维护服务实现（清空处理历史 / 重置所有配置）</summary>
/// <remarks>
/// 走 ExecuteDeleteAsync 直发 DELETE 语句（不走 EF 跟踪）；重置后调 IDataSeeder.SeedAsync 自动补默认分类与解析规则。
/// ResetConfig 删 Parse_AiProvider 会经 FK CASCADE 连带清除其 Audit_AiCall 调用统计（r4 P3-r4.4 澄清，属预期副作用）。
/// 使用独立 DbContext（避免与并发请求共享同一实例 — 见 CLAUDE.md §八 EF Core 并发红线）。
/// 9000 错误由 ExceptionHandlerMiddleware 统一兜底（DB IO 异常透传）。
/// </remarks>
internal sealed class SystemMaintenanceService : ISystemMaintenanceService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly IDataSeeder _seeder;
    private readonly ILogger<SystemMaintenanceService> _logger;

    public SystemMaintenanceService(
        IDbContextFactory<PmmDbContext> dbFactory,
        IDataSeeder seeder,
        ILogger<SystemMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _seeder = seeder;
        _logger = logger;
    }

    public async Task<ClearHistoryResult> ClearHistoryAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // 先删依赖表（FK 顺序），最后删主表 Media_Item
        int processSteps = await db.ProcessSteps.ExecuteDeleteAsync(ct);
        int aiCalls = await db.AuditAiCalls.ExecuteDeleteAsync(ct);
        int operations = await db.AuditOperations.ExecuteDeleteAsync(ct);
        int scheduledRuns = await db.AuditScheduledTaskRuns.ExecuteDeleteAsync(ct);
        int deliveries = await db.WebhookDeliveries.ExecuteDeleteAsync(ct);
        int mediaItems = await db.MediaItems.ExecuteDeleteAsync(ct);

        _logger.LogWarning("已清空处理历史：MediaItem={Media} ProcessStep={Steps} AiCall={Ai} Operation={Op} ScheduledTaskRun={Sched} WebhookDelivery={Wh}",
            mediaItems, processSteps, aiCalls, operations, scheduledRuns, deliveries);

        return new ClearHistoryResult(mediaItems, processSteps, aiCalls, operations, scheduledRuns, deliveries);
    }

    public async Task<ResetConfigResult> ResetConfigAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // FK 顺序：先删依赖（CategoryMatchRule 依赖 CategoryDefinition）
        int categoryRules = await db.CategoryMatchRules.ExecuteDeleteAsync(ct);
        int categories = await db.CategoryDefinitions.ExecuteDeleteAsync(ct);
        int watchFolders = await db.WatchFolders.ExecuteDeleteAsync(ct);
        int parseRules = await db.ParseRules.ExecuteDeleteAsync(ct);
        int parseAi = await db.ParseAiProviders.ExecuteDeleteAsync(ct);
        int tmdbSettings = await db.TmdbSettings.ExecuteDeleteAsync(ct);
        int webhookSubs = await db.WebhookSubscriptions.ExecuteDeleteAsync(ct);

        // System_Setting 删除排除 System.SetupCompleted 标记：否则重置后重启会被 SetupGuardMiddleware
        // 判为「未初始化」拦截全部请求要求重新建管理员。其余 KV（Proxy/Update/Webhook 开关/留存配置）删除后
        // 各服务回落代码默认值，等价「重置为出厂默认」。
        int systemSettings = await db.SystemSettings
            .Where(s => s.Key != SetupService.SetupCompletedKey)
            .ExecuteDeleteAsync(ct);

        // 注意：Watch_IgnoreRule（16 条下载器临时文件忽略规则）与 System_MediaExtension 同为 migration HasData
        // 出厂基线，且无代码层 fallback——删了 DataSeeder 不会重建（SeedAsync 只补分类 + 解析规则），会导致
        // 文件监控不再忽略下载临时文件 / 媒体格式全失。故二者均「不删」，重置后保持出厂基线。
        await _seeder.SeedAsync(ct);

        _logger.LogWarning("已重置所有配置并重新种子：SystemSetting={Sys} Watch={Wf} Parse={Pr}/{Ai} Tmdb={Ts} Category={Cd}/{Cmr} WebhookSub={Ws}（保留 Watch_IgnoreRule / System_MediaExtension 出厂基线与 SetupCompleted 标记）",
            systemSettings, watchFolders, parseRules, parseAi,
            tmdbSettings, categories, categoryRules, webhookSubs);

        return new ResetConfigResult(
            systemSettings, watchFolders, WatchIgnoreRules: 0,
            parseRules, parseAi, tmdbSettings,
            categories, categoryRules, webhookSubs,
            ReseededDefaults: true);
    }
}
