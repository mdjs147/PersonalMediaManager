using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl.Triggers;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Application.Services.System;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Infrastructure.Platform.Scheduling;
using PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

namespace PersonalMediaManager.Application.Tests.Scheduling;

/// <summary>Quartz 使用进程级 scheduler 仓储，本集合禁止与其它测试并行争用固定名称和线程池</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QuartzSchedulingCollection
{
    public const string Name = "Quartz scheduling";
}

/// <summary>QuartzModule（D5.1）— 调度模块注册与最小行为验证</summary>
/// <remarks>
/// 不引 HostedService（避免 Quartz 主循环阻塞测试），直接通过 ISchedulerFactory 拿 IScheduler 手动 Start / Shutdown，
/// 验证：DI 拿得到、In-Memory store 工作、Job 能调度并实际触发一次。
/// Quartz 按固定 SchedulerName 注册到进程级仓储；凡取得 scheduler 的测试都必须 Shutdown，避免实例泄漏污染后续用例。
/// </remarks>
[Collection(QuartzSchedulingCollection.Name)]
public sealed class QuartzModuleTests
{
    [Fact]
    public async Task AddQuartzScheduling_Registers_SchedulerFactory()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQuartzScheduling();

        await using ServiceProvider sp = services.BuildServiceProvider();

        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        factory.Should().NotBeNull();
    }

    [Fact]
    public async Task SchedulerFactory_GetScheduler_Returns_NonNull_Scheduler()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQuartzScheduling();

        await using ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();

        IScheduler scheduler = await factory.GetScheduler();
        scheduler.Should().NotBeNull();
        scheduler.SchedulerName.Should().Be("PersonalMediaManager");
        await scheduler.Shutdown(waitForJobsToComplete: false);
    }

    [Fact]
    public async Task Scheduler_Start_And_TriggerJob_Executes_Job_Once()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQuartzScheduling();

        await using ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        IScheduler scheduler = await factory.GetScheduler();

        ProbeJob.Reset();
        await scheduler.Start();
        try
        {
            IJobDetail job = JobBuilder.Create<ProbeJob>()
                .WithIdentity("probe", "test")
                .Build();
            ITrigger trigger = TriggerBuilder.Create()
                .WithIdentity("probe-trigger", "test")
                .StartNow()
                .Build();
            await scheduler.ScheduleJob(job, trigger);

            bool fired = await ProbeJob.FiredSignal.WaitAsync(TimeSpan.FromSeconds(10));
            fired.Should().BeTrue("InMemory store 调度的 Job 必须在 10 秒内触发（并行测试场景下宽容窗口）");
            ProbeJob.ExecuteCount.Should().Be(1);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
    }

    [Fact]
    public async Task Scheduler_IsSameInstance_Across_GetScheduler_Calls()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQuartzScheduling();

        await using ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();

        IScheduler s1 = await factory.GetScheduler();
        IScheduler s2 = await factory.GetScheduler();
        s1.Should().BeSameAs(s2, "PMM 全局只需一个 Scheduler 实例承担所有 Job 调度");
        await s1.Shutdown(waitForJobsToComplete: false);
    }

    [Fact]
    public async Task AddQuartzScheduling_Registers_All_Six_PmmJobs_With_Default_Triggers()
    {
        // 历史回归：AddQuartzScheduling 内已声明追加 FullScan / LogRetention / AiCallRetention / Backup / WebhookRetry / TmdbZeroResultRetry 六个 Job；
        // 曾因仅在 PmmJobsRegistration 暴露扩展方法、却无任何调用方而导致生产环境 Job 永不触发。
        // 本测试守护「QuartzModule 内四个 Job 集中注册」这条不变式，防止后续重构再次回退到「声明而不装配」。
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // Job 依赖：scheduler 装配阶段不会真正实例化 Job，但补齐这些 service 让后续可能的 ValidateOnBuild / Job 解析也能通
        services.AddSingleton(Substitute.For<IFullScanCoordinator>());
        services.AddSingleton(Substitute.For<IWebhookRetryCoordinator>());
        services.AddSingleton(Substitute.For<IAiCallRetentionService>());
        services.AddSingleton(Substitute.For<IScheduledTaskRunRetentionService>());
        services.AddSingleton(Substitute.For<IBackupService>());
        services.AddSingleton(Substitute.For<ITmdbZeroResultRetrySweeper>());
        services.AddSingleton(AppPaths.ForRoot(Path.Combine(Path.GetTempPath(), $"pmm-quartz-{Guid.NewGuid():N}")));
        services.AddSingleton<IClock>(new FakeUtcClock(new DateTimeOffset(2026, 5, 24, 0, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<IScheduledTaskRunRecorder>(new PassThroughTaskRunRecorder());

        services.AddQuartzScheduling();

        await using ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        IScheduler scheduler = await factory.GetScheduler();

        // 1. FullScan：默认 12h SimpleTrigger + FireNow misfire
        IJobDetail? fullScan = await scheduler.GetJobDetail(FullScanJob.Key);
        fullScan.Should().NotBeNull("AddQuartzScheduling 必须装配 FullScanJob，否则生产环境 12h 周期扫描永不触发");
        fullScan!.JobType.Should().Be<FullScanJob>();
        SimpleTriggerImpl fullScanTrigger = (await scheduler.GetTriggersOfJob(FullScanJob.Key))
            .Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        fullScanTrigger.RepeatInterval.Should().Be(PmmJobsRegistration.DefaultFullScanInterval);
        fullScanTrigger.MisfireInstruction.Should().Be(MisfireInstruction.SimpleTrigger.FireNow);

        // 2. LogRetention：每天 03:00 Cron
        IJobDetail? logRetention = await scheduler.GetJobDetail(LogRetentionJob.Key);
        logRetention.Should().NotBeNull("AddQuartzScheduling 必须装配 LogRetentionJob，否则日志目录会无限增长");
        logRetention!.JobType.Should().Be<LogRetentionJob>();
        CronTriggerImpl logRetentionTrigger = (await scheduler.GetTriggersOfJob(LogRetentionJob.Key))
            .Single().Should().BeOfType<CronTriggerImpl>().Subject;
        logRetentionTrigger.CronExpressionString.Should().Be(PmmJobsRegistration.DefaultLogRetentionCron);

        // 3. WebhookRetry：1min SimpleTrigger + NextWithRemainingCount misfire
        IJobDetail? webhookRetry = await scheduler.GetJobDetail(WebhookRetryJob.Key);
        webhookRetry.Should().NotBeNull("AddQuartzScheduling 必须装配 WebhookRetryJob，否则 Outbox 漏单 / 进程重启后会有投递永停 Pending");
        webhookRetry!.JobType.Should().Be<WebhookRetryJob>();
        SimpleTriggerImpl webhookRetryTrigger = (await scheduler.GetTriggersOfJob(WebhookRetryJob.Key))
            .Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        webhookRetryTrigger.RepeatInterval.Should().Be(PmmJobsRegistration.DefaultWebhookRetryInterval);
        webhookRetryTrigger.MisfireInstruction.Should().Be(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount);

        // 4. AiCallRetention：每天 03:30 Cron（错开 LogRetention 03:00）
        IJobDetail? aiCallRetention = await scheduler.GetJobDetail(AiCallRetentionJob.Key);
        aiCallRetention.Should().NotBeNull("AddQuartzScheduling 必须装配 AiCallRetentionJob，否则 Audit_AiCall 调用日志（含原文）会无限增长");
        aiCallRetention!.JobType.Should().Be<AiCallRetentionJob>();
        CronTriggerImpl aiCallRetentionTrigger = (await scheduler.GetTriggersOfJob(AiCallRetentionJob.Key))
            .Single().Should().BeOfType<CronTriggerImpl>().Subject;
        aiCallRetentionTrigger.CronExpressionString.Should().Be(PmmJobsRegistration.DefaultAiCallRetentionCron);

        // 5. Backup：每天 04:00 Cron（错开 LogRetention 03:00 / AiCallRetention 03:30）
        IJobDetail? backup = await scheduler.GetJobDetail(BackupJob.Key);
        backup.Should().NotBeNull("AddQuartzScheduling 必须装配 BackupJob，否则数据库无定时在线快照备份（真实数据丢失风险）");
        backup!.JobType.Should().Be<BackupJob>();
        CronTriggerImpl backupTrigger = (await scheduler.GetTriggersOfJob(BackupJob.Key))
            .Single().Should().BeOfType<CronTriggerImpl>().Subject;
        backupTrigger.CronExpressionString.Should().Be(PmmJobsRegistration.DefaultBackupCron);

        // 6. TmdbZeroResultRetry：6h SimpleTrigger + NextWithRemainingCount misfire（Sweeper 有 20h 时距门槛，补偿触发只会空转）
        IJobDetail? zeroResultRetry = await scheduler.GetJobDetail(TmdbZeroResultRetryJob.Key);
        zeroResultRetry.Should().NotBeNull("AddQuartzScheduling 必须装配 TmdbZeroResultRetryJob，否则「TMDB 未收录」待确认项永远不会自动重试");
        zeroResultRetry!.JobType.Should().Be<TmdbZeroResultRetryJob>();
        SimpleTriggerImpl zeroResultRetryTrigger = (await scheduler.GetTriggersOfJob(TmdbZeroResultRetryJob.Key))
            .Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        zeroResultRetryTrigger.RepeatInterval.Should().Be(PmmJobsRegistration.DefaultTmdbZeroResultRetryInterval);
        zeroResultRetryTrigger.MisfireInstruction.Should().Be(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount);

        // 同步校验：scheduler 内的 JobKey 集合刚好等于这六个，防止后续误增 Job 又未补齐测试
        IReadOnlyCollection<JobKey> allKeys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
        allKeys.Should().BeEquivalentTo(new[] { FullScanJob.Key, LogRetentionJob.Key, AiCallRetentionJob.Key, BackupJob.Key, WebhookRetryJob.Key, TmdbZeroResultRetryJob.Key });
        await scheduler.Shutdown(waitForJobsToComplete: false);
    }

    [Fact]
    public async Task Scoped_Service_Resolved_From_PerExecution_Scope_Not_Singleton()
    {
        // r3 P2-r3.18：守护 Quartz.Extensions.DependencyInjection 默认 JobFactory 「每次 Execute 建独立 scope」契约
        // 若后续重构换成 root provider 解析（典型反模式：q.UseJobFactory<ServiceProviderJobFactory>()），
        // ScopedProbe 的两次实例 ID 会相等 → Recorder（Scoped）变 captive，本测试翻车提示根因
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<ScopedProbe>();
        services.AddQuartzScheduling();

        await using ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        IScheduler scheduler = await factory.GetScheduler();

        ScopedProbeJob.CapturedIds.Clear();
        ScopedProbeJob.FiredSignal = new SemaphoreSlim(0, 2);

        await scheduler.Start();
        try
        {
            // 触发两次同一 Job，每次都应在不同 scope 里跑 → ScopedProbe 实例 ID 应不同
            for (int i = 0; i < 2; i++)
            {
                IJobDetail job = JobBuilder.Create<ScopedProbeJob>()
                    .WithIdentity($"scoped-probe-{i}", "test")
                    .Build();
                ITrigger trigger = TriggerBuilder.Create()
                    .WithIdentity($"scoped-probe-trigger-{i}", "test")
                    .StartNow()
                    .Build();
                await scheduler.ScheduleJob(job, trigger);
            }

            (await ScopedProbeJob.FiredSignal.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            (await ScopedProbeJob.FiredSignal.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

            ScopedProbeJob.CapturedIds.Should().HaveCount(2);
            ScopedProbeJob.CapturedIds[0].Should().NotBe(ScopedProbeJob.CapturedIds[1],
                "Quartz JobFactory 必须为每次 Execute 建独立 scope，Scoped 依赖才不会变 captive；"
                + "若两次相同则说明 JobFactory 退化为 root provider 解析");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
    }

    [DisallowConcurrentExecution]
    private sealed class ProbeJob : IJob
    {
        public static readonly SemaphoreSlim FiredSignal = new(0, 1);
        public static int ExecuteCount;

        public static void Reset()
        {
            ExecuteCount = 0;
            while (FiredSignal.CurrentCount > 0)
            {
                FiredSignal.Wait(0);
            }
        }

        public Task Execute(IJobExecutionContext context)
        {
            Interlocked.Increment(ref ExecuteCount);
            FiredSignal.Release();
            return Task.CompletedTask;
        }
    }

    /// <summary>Scoped 探针：每个 scope 一个独立实例 ID，用于验证 Job 是否走独立 scope</summary>
    private sealed class ScopedProbe
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    /// <summary>Job 注入 Scoped 服务，把每次 Execute 拿到的实例 ID 记录下来供测试断言</summary>
    private sealed class ScopedProbeJob : IJob
    {
        public static readonly List<Guid> CapturedIds = new();
        public static SemaphoreSlim FiredSignal = new(0, 2);
        private static readonly Lock Sync = new();

        private readonly ScopedProbe _probe;

        public ScopedProbeJob(ScopedProbe probe) { _probe = probe; }

        public Task Execute(IJobExecutionContext context)
        {
            lock (Sync)
            {
                CapturedIds.Add(_probe.Id);
            }
            FiredSignal.Release();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUtcClock : IClock
    {
        public FakeUtcClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }
}
