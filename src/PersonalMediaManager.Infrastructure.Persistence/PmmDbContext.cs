using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Aggregates.ParseTestCases;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence;

/// <summary>PersonalMediaManager 主数据库上下文（SQLite + EF Core 10）</summary>
/// <remarks>
/// 注册要点（数据库设计 §3.2）：必须使用 AddDbContextFactory&lt;PmmDbContext&gt; 而非 AddDbContext，
/// 后台 Worker / SignalR Hub 等需并发场景从工厂取独立实例，响应 CLAUDE.md §八「同 DbContext 不可 Task.WhenAll」红线。
/// OnModelCreating 通过 ApplyConfigurationsFromAssembly 集中加载 Configurations/ 下全部 IEntityTypeConfiguration&lt;T&gt;（每表一个，随库表演进增减，勿在此硬编码计数）；
/// 拦截器（TimestampInterceptor / RowVersionInterceptor）由 DependencyInjection.AddInfrastructurePersistence 注入。
/// </remarks>
public sealed class PmmDbContext : DbContext
{
    public PmmDbContext(DbContextOptions<PmmDbContext> options) : base(options) { }

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<SystemMediaExtension> SystemMediaExtensions => Set<SystemMediaExtension>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<WatchFolder> WatchFolders => Set<WatchFolder>();
    public DbSet<WatchIgnoreRule> WatchIgnoreRules => Set<WatchIgnoreRule>();
    public DbSet<ParseRule> ParseRules => Set<ParseRule>();
    public DbSet<ParseTestCase> ParseTestCases => Set<ParseTestCase>();
    public DbSet<ParseAiProvider> ParseAiProviders => Set<ParseAiProvider>();
    public DbSet<TmdbSetting> TmdbSettings => Set<TmdbSetting>();
    public DbSet<TmdbMetadataCache> TmdbMetadataCaches => Set<TmdbMetadataCache>();
    public DbSet<TmdbSearchCache> TmdbSearchCaches => Set<TmdbSearchCache>();
    public DbSet<CategoryDefinition> CategoryDefinitions => Set<CategoryDefinition>();
    public DbSet<CategoryMatchRule> CategoryMatchRules => Set<CategoryMatchRule>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<ProcessStep> ProcessSteps => Set<ProcessStep>();

    // 媒体作品聚合 + 富化关联（按 TMDB 作品聚合的库内持久实体）
    public DbSet<MediaWork> MediaWorks => Set<MediaWork>();
    public DbSet<MediaWorkCredit> MediaWorkCredits => Set<MediaWorkCredit>();
    public DbSet<MediaWorkGenre> MediaWorkGenres => Set<MediaWorkGenre>();
    public DbSet<MediaWorkCompany> MediaWorkCompanies => Set<MediaWorkCompany>();
    public DbSet<MediaWorkNetwork> MediaWorkNetworks => Set<MediaWorkNetwork>();
    public DbSet<MediaWorkKeyword> MediaWorkKeywords => Set<MediaWorkKeyword>();
    public DbSet<MediaSeason> MediaSeasons => Set<MediaSeason>();
    public DbSet<MediaEpisode> MediaEpisodes => Set<MediaEpisode>();
    public DbSet<MediaPerson> MediaPersons => Set<MediaPerson>();
    public DbSet<MediaGenre> MediaGenres => Set<MediaGenre>();
    public DbSet<MediaCompany> MediaCompanies => Set<MediaCompany>();
    public DbSet<MediaNetwork> MediaNetworks => Set<MediaNetwork>();
    public DbSet<MediaKeyword> MediaKeywords => Set<MediaKeyword>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<AuditOperation> AuditOperations => Set<AuditOperation>();
    public DbSet<AuditAiCall> AuditAiCalls => Set<AuditAiCall>();
    public DbSet<AuditScheduledTaskRun> AuditScheduledTaskRuns => Set<AuditScheduledTaskRun>();

    // 字幕下载（外部字幕源单例配置 + 成功下载记录）
    public DbSet<SubtitleSetting>  SubtitleSettings  => Set<SubtitleSetting>();
    public DbSet<SubtitleDownload> SubtitleDownloads => Set<SubtitleDownload>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PmmDbContext).Assembly);

        // SQLite 默认把 DateTimeOffset 存为 TEXT，>=/&lt; 比较不可翻译到 SQL。
        // 全表统一用 DateTimeOffsetToBinaryConverter 存为 long（保留 Offset 信息），
        // 让 AiProviderHealthTracker / TmdbSearchService 的窗口比较走真实 SQL 索引而非客户端过滤。
        DateTimeOffsetToBinaryConverter converter = new();
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty prop in entityType.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(converter);
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
