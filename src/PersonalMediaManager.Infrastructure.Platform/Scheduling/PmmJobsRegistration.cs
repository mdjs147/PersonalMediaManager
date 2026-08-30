using Quartz;
using PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling;

/// <summary>PMM 调度任务集中注册</summary>
/// <remarks>
/// 与 QuartzModule 切分：QuartzModule 装基础设施（store / 线程池 / HostedService）并默认间隔注册三个 Job，
/// 本类暴露的扩展方法供 QuartzModule 与单测复用——单测可传入自定义 interval / cron 验证触发器配置。
/// 默认参数（12h / 03:00 / 1min）也由本类常量集中持有，调整周期改这里即可。
/// </remarks>
public static class PmmJobsRegistration
{
    /// <summary>默认全量扫描周期：12 小时</summary>
    public static readonly TimeSpan DefaultFullScanInterval = TimeSpan.FromHours(12);

    /// <summary>日志清理默认 Cron：每天 03:00（本地时区），避开主流量时段</summary>
    public const string DefaultLogRetentionCron = "0 0 3 * * ?";

    /// <summary>AI 调用日志清理默认 Cron：每天 03:30（错开文件日志清理 03:00）</summary>
    public const string DefaultAiCallRetentionCron = "0 30 3 * * ?";

    /// <summary>数据库自动备份默认 Cron：每天 04:00（错开 LogRetention 03:00 / AiCallRetention 03:30）</summary>
    public const string DefaultBackupCron = "0 0 4 * * ?";

    /// <summary>Webhook 重试默认扫描周期：1 分钟（与最短退避 30s 同量级）</summary>
    public static readonly TimeSpan DefaultWebhookRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>「TMDB 未收录」自动重试默认扫描周期：6 小时（每天最多重投一次由 Sweeper 的 20h 条件保证）</summary>
    public static readonly TimeSpan DefaultTmdbZeroResultRetryInterval = TimeSpan.FromHours(6);

    /// <summary>在 AddQuartz 配置块内注册 FullScanJob + 周期触发器</summary>
    /// <remarks>
    /// StartAt = now + 30s：服务启动后 30 秒再首次扫描，避开启动期 DB 迁移 / 文件监听器装配高峰；
    /// SimpleSchedule 重复间隔（默认 12h）；
    /// WithMisfireHandlingInstructionFireNow：进程睡眠 / 暂停错过触发后立即补一次（FullScan 是补偿任务，多跑一次不会出错）。
    /// </remarks>
    public static IServiceCollectionQuartzConfigurator AddFullScanJob(
        this IServiceCollectionQuartzConfigurator q,
        TimeSpan? interval = null)
    {
        TimeSpan period = interval ?? DefaultFullScanInterval;

        q.AddJob<FullScanJob>(j => j
            .WithIdentity(FullScanJob.Key)
            .StoreDurably());

        q.AddTrigger(t => t
            .WithIdentity("full-scan-trigger", "scan")
            .ForJob(FullScanJob.Key)
            .StartAt(DateBuilder.FutureDate(30, IntervalUnit.Second))
            .WithSimpleSchedule(s => s
                .WithInterval(period)
                .RepeatForever()
                .WithMisfireHandlingInstructionFireNow()));

        return q;
    }

    /// <summary>在 AddQuartz 配置块内注册 LogRetentionJob + 日清 Cron 触发器</summary>
    /// <remarks>
    /// Cron 表达式默认每天 03:00 触发；
    /// WithMisfireHandlingInstructionFireAndProceed：进程睡眠错过当日触发后醒来立即补一次再回到正常节奏。
    /// </remarks>
    public static IServiceCollectionQuartzConfigurator AddLogRetentionJob(
        this IServiceCollectionQuartzConfigurator q,
        string? cronExpression = null)
    {
        string cron = cronExpression ?? DefaultLogRetentionCron;

        q.AddJob<LogRetentionJob>(j => j
            .WithIdentity(LogRetentionJob.Key)
            .StoreDurably());

        q.AddTrigger(t => t
            .WithIdentity("log-retention-trigger", "maintenance")
            .ForJob(LogRetentionJob.Key)
            .WithCronSchedule(cron, c => c.WithMisfireHandlingInstructionFireAndProceed()));

        return q;
    }

    /// <summary>在 AddQuartz 配置块内注册 AiCallRetentionJob + 日清 Cron 触发器</summary>
    /// <remarks>
    /// Cron 默认每天 03:30 触发（错开 LogRetention 03:00）；
    /// WithMisfireHandlingInstructionFireAndProceed：进程睡眠错过当日触发后醒来立即补一次再回到正常节奏。
    /// </remarks>
    public static IServiceCollectionQuartzConfigurator AddAiCallRetentionJob(
        this IServiceCollectionQuartzConfigurator q,
        string? cronExpression = null)
    {
        string cron = cronExpression ?? DefaultAiCallRetentionCron;

        q.AddJob<AiCallRetentionJob>(j => j
            .WithIdentity(AiCallRetentionJob.Key)
            .StoreDurably());

        q.AddTrigger(t => t
            .WithIdentity("aicall-retention-trigger", "maintenance")
            .ForJob(AiCallRetentionJob.Key)
            .WithCronSchedule(cron, c => c.WithMisfireHandlingInstructionFireAndProceed()));

        return q;
    }

    /// <summary>在 AddQuartz 配置块内注册 BackupJob + 日备 Cron 触发器</summary>
    /// <remarks>
    /// Cron 默认每天 04:00 触发（错开 LogRetention 03:00 / AiCallRetention 03:30）；
    /// WithMisfireHandlingInstructionFireAndProceed：进程睡眠错过当日触发后醒来立即补一次再回到正常节奏（避免漏备份）。
    /// </remarks>
    public static IServiceCollectionQuartzConfigurator AddBackupJob(
        this IServiceCollectionQuartzConfigurator q,
        string? cronExpression = null)
    {
        string cron = cronExpression ?? DefaultBackupCron;

        q.AddJob<BackupJob>(j => j
            .WithIdentity(BackupJob.Key)
            .StoreDurably());

        q.AddTrigger(t => t
            .WithIdentity("backup-trigger", "maintenance")
            .ForJob(BackupJob.Key)
            .WithCronSchedule(cron, c => c.WithMisfireHandlingInstructionFireAndProceed()));

        return q;
    }

    /// <summary>在 AddQuartz 配置块内注册 TmdbZeroResultRetryJob + 6 小时周期触发器</summary>
    /// <remarks>
    /// StartAt = now + 5min：避开启动期 DB 迁移 / 文件监听装配 / FullScan 首扫（30s）高峰；
    /// SimpleSchedule 默认间隔 6 小时；
    /// WithMisfireHandlingInstructionNextWithRemainingCount：错过的窗口直接放弃等下一轮
    /// （Sweeper 有 20h 时距门槛，补偿性密集触发只会空转，不如顺延）。
    /// </remarks>
    public static IServiceCollectionQuartzConfigurator AddTmdbZeroResultRetryJob(
        this IServiceCollectionQuartzConfigurator q,
        TimeSpan? interval = null)
    {
        TimeSpan period = interval ?? DefaultTmdbZeroResultRetryInterval;

        q.AddJob<TmdbZeroResultRetryJob>(j => j
            .WithIdentity(TmdbZeroResultRetryJob.Key)
            .StoreDurably());

        q.AddTrigger(t => t
            .WithIdentity("tmdb-zero-result-retry-trigger", "parse")
            .ForJob(TmdbZeroResultRetryJob.Key)
            .StartAt(DateBuilder.FutureDate(300, IntervalUnit.Second))
            .WithSimpleSchedule(s => s
                .WithInterval(period)
                .RepeatForever()
                .WithMisfireHandlingInstructionNextWithRemainingCount()));

        return q;
    }

    /// <summary>在 AddQuartz 配置块内注册 WebhookRetryJob + 1 分钟周期触发器</summary>
    /// <remarks>
    /// StartAt = now + 1min：避开启动期 DB 迁移 + Outbox channel 初始化；
    /// SimpleSchedule 默认间隔 1 分钟；
    /// WithMisfireHandlingInstructionNextWithRemainingCount：错过的窗口直接放弃，等下一个整点（避免补偿性扫描堆积重复入队）。
    /// </remarks>
    public static IServiceCollectionQuartzConfigurator AddWebhookRetryJob(
        this IServiceCollectionQuartzConfigurator q,
        TimeSpan? interval = null)
    {
        TimeSpan period = interval ?? DefaultWebhookRetryInterval;

        q.AddJob<WebhookRetryJob>(j => j
            .WithIdentity(WebhookRetryJob.Key)
            .StoreDurably());

        q.AddTrigger(t => t
            .WithIdentity("webhook-retry-trigger", "webhook")
            .ForJob(WebhookRetryJob.Key)
            .StartAt(DateBuilder.FutureDate(60, IntervalUnit.Second))
            .WithSimpleSchedule(s => s
                .WithInterval(period)
                .RepeatForever()
                .WithMisfireHandlingInstructionNextWithRemainingCount()));

        return q;
    }
}
