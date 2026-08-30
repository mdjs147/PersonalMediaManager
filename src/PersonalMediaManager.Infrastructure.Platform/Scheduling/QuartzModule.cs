using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace PersonalMediaManager.Infrastructure.Platform.Scheduling;

/// <summary>Quartz 调度模块注册入口</summary>
/// <remarks>
/// PMM 是单进程本地服务，调度任务（FullScan / LogRetention / WebhookRetry）只需进程生命周期内可用，
/// 无需持久化到 SQLite——因此固定使用 In-Memory store（RAMJobStore），重启后由 HostedService 重新装配。
/// 线程池上限 4：调度任务本身轻量（触发后把工作派发给 Application 服务执行），
/// 4 足以并行触发 FullScan / LogRetention / WebhookRetry 而不至于挤占主管线的 IO / CPU。
/// 关停时 WaitForJobsToComplete=true 防止 LogRetention 删到一半被切断、Webhook 重试调度丢失上下文。
/// PMM 三个 Job（FullScan / LogRetention / WebhookRetry）随 AddQuartz 配置块一并注册——
/// 历史上曾分离到 PmmJobsRegistration.AddPmmScheduledJobs 待 Host 显式调用，但实际从未被调用过，
/// 导致生产环境三个 Job 永不触发；现统一在此处装配，关停 / 启动 / Job 注册三件事都由 AddQuartzScheduling 一口气搞定。
///
/// r3 P2-r3.18：Job 与 IScheduledTaskRunRecorder（Scoped）生命周期校验
///   Quartz.Extensions.DependencyInjection 3.x 默认 JobFactory 是 MicrosoftDependencyInjectionJobFactory，
///   每次 Job 触发会调用 IServiceProvider.CreateScope() 创建独立 scope，并从该 scope 解析 Job 实例。
///   因此：
///     · Job 类（FullScanJob / WebhookRetryJob / LogRetentionJob）由 AddJob&lt;T&gt;() 注册为 Transient，
///       每次触发 New 一个，scope 内独占；
///     · IScheduledTaskRunRecorder（Scoped，见 Infrastructure.Persistence.ServiceCollectionExtensions:67）+
///       IFullScanCoordinator + IWebhookRetryCoordinator 等 Scoped 依赖均从该 scope 解析，无 captive。
///     · Recorder 内部用 IDbContextFactory.CreateDbContext() 独立短期 ctx（双 ctx 模式），不持有 Scoped DbContext，
///       即使 scope 被 Quartz 在 Job 跑很久时回收也不影响。
///   该约束由 QuartzModuleTests.Scoped_Service_Resolved_From_PerExecution_Scope 兜底，
///   防止后续重构改回 root provider（典型反模式：q.UseJobFactory&lt;ServiceProviderJobFactory&gt;()）。
/// </remarks>
public static class QuartzModule
{
    /// <summary>注册 Quartz：In-Memory store + 4 线程池 + 三个 PMM Job + 优雅关停</summary>
    public static IServiceCollection AddQuartzScheduling(this IServiceCollection services)
    {
        services.AddQuartz(q =>
        {
            q.SchedulerId = "PersonalMediaManager-Scheduler";
            q.SchedulerName = "PersonalMediaManager";
            q.UseSimpleTypeLoader();
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 4);
            // 不显式 q.UseJobFactory()：保留默认 MicrosoftDependencyInjectionJobFactory，
            // 它每次 Execute 会 CreateScope() 后再解析 Job + 所有依赖，让 Scoped Recorder 安全可用（r3 P2-r3.18）

            q.AddFullScanJob();
            q.AddLogRetentionJob();
            q.AddAiCallRetentionJob();
            q.AddBackupJob();
            q.AddWebhookRetryJob();
            q.AddTmdbZeroResultRetryJob();
        });

        services.AddQuartzHostedService(opts =>
        {
            opts.WaitForJobsToComplete = true;
            opts.AwaitApplicationStarted = true;
        });

        return services;
    }
}
