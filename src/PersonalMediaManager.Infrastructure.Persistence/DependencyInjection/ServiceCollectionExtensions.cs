using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Account;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Auth;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Category;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Dashboard;
using PersonalMediaManager.Application.Services.History;
using PersonalMediaManager.Application.Services.Library;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Review;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Application.Services.Search;
using PersonalMediaManager.Application.Services.Settings;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Application.Services.Statistics;
using PersonalMediaManager.Application.Services.Subtitles;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Application.Services.Tmdb;
using PersonalMediaManager.Application.Services.Watch;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;
using PersonalMediaManager.Infrastructure.Persistence.Services.Account;
using PersonalMediaManager.Infrastructure.Persistence.Services.Audit;
using PersonalMediaManager.Infrastructure.Persistence.Services.Auth;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;
using PersonalMediaManager.Infrastructure.Persistence.Services.Category;
using PersonalMediaManager.Infrastructure.Persistence.Services.Classify;
using PersonalMediaManager.Infrastructure.Persistence.Services.Dashboard;
using PersonalMediaManager.Infrastructure.Persistence.Services.History;
using PersonalMediaManager.Infrastructure.Persistence.Services.Library;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;
using PersonalMediaManager.Infrastructure.Persistence.Services.Proxy;
using PersonalMediaManager.Infrastructure.Persistence.Services.Review;
using PersonalMediaManager.Infrastructure.Persistence.Services.Scan;
using PersonalMediaManager.Infrastructure.Persistence.Services.Search;
using PersonalMediaManager.Infrastructure.Persistence.Services.Settings;
using PersonalMediaManager.Infrastructure.Persistence.Services.Statistics;
using PersonalMediaManager.Infrastructure.Persistence.Services.Maintenance;
using PersonalMediaManager.Infrastructure.Persistence.Services.Setup;
using PersonalMediaManager.Infrastructure.Persistence.Services.Subtitles;
using PersonalMediaManager.Infrastructure.Persistence.Services.Tmdb;
using PersonalMediaManager.Infrastructure.Persistence.Services.Update;
using PersonalMediaManager.Infrastructure.Persistence.Services.Versioning;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Persistence.Services.Watch;
using PersonalMediaManager.Infrastructure.Persistence.Services.Webhook;

namespace PersonalMediaManager.Infrastructure.Persistence.DependencyInjection;

/// <summary>Infrastructure.Persistence 注入扩展</summary>
/// <remarks>
/// 必须用 AddDbContextFactory（CLAUDE.md §八 红线：同 DbContext 不可 Task.WhenAll）；
/// 拦截器走单例（无状态）；连接串由调用方传入（Launcher 在 AppPaths 下解析得到 SQLite 文件路径）。
/// </remarks>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructurePersistence(
        this IServiceCollection services,
        string sqliteConnectionString)
    {
        services.AddSingleton<TimestampInterceptor>();
        services.AddSingleton<RowVersionInterceptor>();
        // 每条连接打开后应用 WAL + busy_timeout 等并发 PRAGMA，根治「database is locked」（SQLITE_BUSY）
        services.AddSingleton<SqlitePragmaInterceptor>();

        services.AddDbContextFactory<PmmDbContext>((sp, opt) =>
        {
            opt.UseSqlite(sqliteConnectionString);
            opt.AddInterceptors(
                sp.GetRequiredService<TimestampInterceptor>(),
                sp.GetRequiredService<RowVersionInterceptor>(),
                sp.GetRequiredService<SqlitePragmaInterceptor>());
        });

        // Application 服务实现（按 §0.5 红线放 Persistence，§十二 接口仍在 Application）
        services.AddScoped<IAuditOperationWriter, AuditOperationWriter>();
        services.AddScoped<IAuditAiCallWriter, AuditAiCallWriter>();
        // Audit_AiCall 留存清理（被 AiCallRetentionJob 每日触发；按天龄 + 单 provider 行数上限双闸）
        services.AddScoped<IAiCallRetentionService, AiCallRetentionService>();
        // Audit_ScheduledTaskRun 留存清理（被 LogRetentionJob 每日顺带触发；固定 30 天天龄闸，含 Running 僵尸行）
        services.AddScoped<IScheduledTaskRunRetentionService, ScheduledTaskRunRetentionService>();
        // 调度任务执行记录器（被 FullScanJob / LogRetentionJob / AiCallRetentionJob / WebhookRetryJob 包裹调用）
        services.AddScoped<IScheduledTaskRunRecorder, ScheduledTaskRunRecorder>();
        services.AddScoped<ISetupService, SetupService>();
        services.AddScoped<IDataSeeder, DataSeeder>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IGeneralSettingsService, GeneralSettingsService>();
        // 归档命名模板（4 个 Archive_ key 读写 + 草稿预览；模板校验复用 NamingTemplateRenderer）
        services.AddScoped<IArchiveNamingService, ArchiveNamingService>();
        // 代理解析（单例：HttpClient handler lifetime 内复用同一 IWebProxy 实例，需线程安全 + 内部 60s 缓存）
        services.AddSingleton<IProxyResolver, ProxyResolver>();
        services.AddScoped<IWatchFolderService, WatchFolderService>();
        // 媒体扩展名：Singleton（IMediaExtensionProvider 内存缓存供高频路径用，IMediaExtensionService 供 CRUD API）
        services.AddSingleton<MediaExtensionService>();
        services.AddSingleton<IMediaExtensionService>(sp => sp.GetRequiredService<MediaExtensionService>());
        services.AddSingleton<IMediaExtensionProvider>(sp => sp.GetRequiredService<MediaExtensionService>());
        services.AddScoped<IWatchIgnoreRuleService, WatchIgnoreRuleService>();
        services.AddScoped<ICategoryDefinitionService, CategoryDefinitionService>();
        services.AddScoped<ICategoryMatchRuleService, CategoryMatchRuleService>();
        services.AddScoped<IParseRuleService, ParseRuleService>();
        services.AddScoped<IParseTestCaseService, ParseTestCaseService>();
        services.AddScoped<IParseAiProviderService, ParseAiProviderService>();
        services.AddScoped<IAiProviderResolver, AiProviderResolver>();
        services.AddScoped<IAiProviderHealthTracker, AiProviderHealthTracker>();
        // 套餐配额计量（调用后计次/计 token + 超限幂等置 QuotaExceededAt 自动禁用 + ai.provider_quota_exceeded 告警）
        services.AddScoped<IAiProviderQuotaTracker, AiProviderQuotaTracker>();
        services.AddScoped<ITmdbSettingService, TmdbSettingService>();
        services.AddScoped<ITmdbSearchService, TmdbSearchService>();
        // 字幕源配置（单例 Id=1，Token 走 DataProtection）+ 字幕下载（Assrt 搜索 / 文件清单 / 下载落盘 / 记录查询）
        services.AddScoped<ISubtitleSettingService, SubtitleSettingService>();
        services.AddScoped<ISubtitleDownloadService, SubtitleDownloadService>();
        services.AddScoped<IWebhookSubscriptionService, WebhookSubscriptionService>();
        services.AddScoped<IWebhookDeliveryService, WebhookDeliveryService>();
        // Webhook 事件统一发射器：把一个事件 fan-out 成 N 条 Delivery + 入 Outbox（收口原 Archive/Backup 内联逻辑，供 ProcessFile 等复用）
        // 单例：依赖全 singleton（IDbContextFactory / IWebhookOutboxQueue / IClock），供 singleton 的 NetworkShareMonitorWorker 经 IAlertService 间接使用
        services.AddSingleton<IWebhookEmitter, WebhookEmitter>();
        // 告警抑制状态（进程内单例）+ 告警服务（抑制窗口 System_AlertSuppressMinutes + 复用 Emitter 发送）：disk.low / share.unreachable / ai.all_unavailable
        services.AddSingleton<AlertSuppressionState>();
        services.AddSingleton<IAlertService, AlertService>();
        // D5.4 重试调度协调器：被 WebhookRetryJob 周期触发，扫描 Pending/Retrying 投递并重入 Outbox channel
        services.AddScoped<IWebhookRetryCoordinator, WebhookRetryCoordinator>();
        // 「TMDB 未收录」自动重试：被 TmdbZeroResultRetryJob 周期触发，按天重投高置信零结果待确认项（收录后自动归档）
        services.AddScoped<ITmdbZeroResultRetrySweeper, TmdbZeroResultRetrySweeper>();
        // D7.1 主编排：依赖 IRuleEngineService / IClassifyService / IArchiveService（D7.2~D7.4 后续填实现）
        // 新发现文件登记入口（落库 Detected 行 + 入队）：Singleton（用 DbContextFactory，无 Scoped 依赖），供 Singleton 的 FileWatcherWorker 注入
        services.AddSingleton<IFileIntakeService, FileIntakeService>();

        services.AddScoped<IProcessFileService, ProcessFileService>();
        // D7.2 规则引擎：用户规则按 Priority 升序 + 内置规则兜底
        services.AddScoped<IRuleEngineService, RuleEngineService>();
        // D7.3 分类：CategoryMatchRule JSON 条件树评估 + 全零命中转 SendToReview
        services.AddScoped<IClassifyService, ClassifyService>();
        // D7.4 归档：路径计算 + 文件落地 + nfo 生成 + Webhook 入 Outbox
        services.AddScoped<IArchiveService, ArchiveService>();
        // D7.5 人工确认队列：list / confirm / ignore / batch-confirm / tmdb-search / bind-tmdb
        services.AddScoped<IReviewService, ReviewService>();
        // D7.6 扫描：手动全量 / 单目录 + 实现 IFullScanCoordinator 供 D5.2 FullScanJob 复用
        services.AddScoped<ScanService>();
        services.AddScoped<IScanService>(sp => sp.GetRequiredService<ScanService>());
        services.AddScoped<IFullScanCoordinator>(sp => sp.GetRequiredService<ScanService>());
        // 整理演练：只读预览解析/匹配/命名，不调 AI / 不动文件 / 不写处理记录
        services.AddScoped<IDryRunService, DryRunService>();
        // D7.7 仪表盘聚合：stats / recent / tasks
        services.AddScoped<IDashboardService, DashboardService>();
        // 统计分析聚合：库构成 / 入库趋势 / 存储 Top（只读匿名，与 Dashboard 同级）
        services.AddScoped<IStatisticsService, StatisticsService>();
        // 仪表盘健康检查（Kestrel uptime / SQLite size / TMDB ping / AI 主提供商 ping）
        services.AddScoped<IHealthCheckService, HealthCheckService>();
        // D7.8 历史：list 过滤分页 + rescan 仅 Failed → Queued 重投
        services.AddScoped<IHistoryService, HistoryService>();
        // 媒体库：已归档作品按 TMDB 聚合的海报墙（只读浏览）
        services.AddScoped<ILibraryService, LibraryService>();
        // 统一全局搜索：历史 / 媒体库 / 待确认三域聚合（单 context 串行查，只读匿名）
        services.AddScoped<ISearchService, SearchService>();
        // 媒体库富化：TMDB 富化详情 → Media_Work + 演职员/类型/公司/电视台/关键词/季集
        services.AddScoped<IWorkEnrichmentService, WorkEnrichmentService>();
        // 富化远端失败短期退避表（进程内单例）：TMDB 不可达时窗口内跳过远端，避免反复打满超时
        services.AddSingleton<WorkEnrichmentBackoff>();
        // 系统维护：清空处理历史 / 重置所有配置（高危 Admin 端点专用）
        services.AddScoped<ISystemMaintenanceService, SystemMaintenanceService>();
        // 数据库自动备份（VACUUM INTO 在线快照 + 密钥环打包 + 按份数保留）：被 BackupJob 每日触发 / SystemController 手动触发
        services.AddScoped<IBackupService, BackupService>();
        // 升级检查（D9.1 / CLAUDE.md §9.5）：System_Setting 5 个 Update_* key 编排 + PAT 加密 + IUpdateChecker 调用
        services.AddScoped<IUpdateSettingService, UpdateSettingService>();
        // 版本号提供方：反射 assembly metadata + 查 __EFMigrationsHistory + 嵌入资源 version-map.json
        // 单例：静态信息构造时缓存，后续调用仅查 db
        services.AddSingleton<IVersionInfoProvider, VersionInfoProvider>();

        return services;
    }
}
