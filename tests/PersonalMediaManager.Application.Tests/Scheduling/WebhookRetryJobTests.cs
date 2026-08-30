using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;
using Quartz.Impl.Triggers;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Infrastructure.Platform.Scheduling;
using PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

namespace PersonalMediaManager.Application.Tests.Scheduling;

/// <summary>WebhookRetryJob（D5.4）— 周期扫描 + 调用协调器 + Trigger 1 分钟周期</summary>
public sealed class WebhookRetryJobTests
{
    [Fact]
    public async Task Execute_CallsCoordinator_SweepAsync_Once()
    {
        IWebhookRetryCoordinator coordinator = Substitute.For<IWebhookRetryCoordinator>();
        coordinator.SweepAsync(Arg.Any<CancellationToken>()).Returns(0);
        WebhookRetryJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<WebhookRetryJob>.Instance);
        IJobExecutionContext ctx = StubContext();

        await job.Execute(ctx);

        await coordinator.Received(1).SweepAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_PassesCancellationToken_FromContext()
    {
        IWebhookRetryCoordinator coordinator = Substitute.For<IWebhookRetryCoordinator>();
        coordinator.SweepAsync(Arg.Any<CancellationToken>()).Returns(3);
        WebhookRetryJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<WebhookRetryJob>.Instance);
        using CancellationTokenSource cts = new();
        IJobExecutionContext ctx = StubContext(cts.Token);

        await job.Execute(ctx);

        await coordinator.Received(1).SweepAsync(cts.Token);
    }

    [Fact]
    public async Task Execute_Swallows_OperationCanceled_WhenContextCancelled()
    {
        IWebhookRetryCoordinator coordinator = Substitute.For<IWebhookRetryCoordinator>();
        coordinator.SweepAsync(Arg.Any<CancellationToken>()).Throws(new OperationCanceledException());
        WebhookRetryJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<WebhookRetryJob>.Instance);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        IJobExecutionContext ctx = StubContext(cts.Token);

        Func<Task> act = async () => await job.Execute(ctx);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Execute_Swallows_BusinessException_AvoidsPollutingMisfirePolicy()
    {
        IWebhookRetryCoordinator coordinator = Substitute.For<IWebhookRetryCoordinator>();
        coordinator.SweepAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("DB 暂时不可达"));
        WebhookRetryJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<WebhookRetryJob>.Instance);
        IJobExecutionContext ctx = StubContext();

        Func<Task> act = async () => await job.Execute(ctx);

        await act.Should().NotThrowAsync(
            "1 分钟周期补偿任务，单次失败靠下一轮兜底，不抛 JobExecutionException");
        await coordinator.Received(1).SweepAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Job_Has_DisallowConcurrentExecution_Attribute()
    {
        typeof(WebhookRetryJob).GetCustomAttributes(typeof(DisallowConcurrentExecutionAttribute), inherit: false)
            .Should().NotBeEmpty("1 分钟周期下若上次未结束又被触发会重复入队同一批 Pending 投递");
    }

    [Fact]
    public async Task Execute_Uses_SkipAuditWhenIdle_To_Silence_Idle_Sweeps()
    {
        IWebhookRetryCoordinator coordinator = Substitute.For<IWebhookRetryCoordinator>();
        coordinator.SweepAsync(Arg.Any<CancellationToken>()).Returns(0);
        PassThroughTaskRunRecorder recorder = new();
        WebhookRetryJob job = new(coordinator, recorder, NullLogger<WebhookRetryJob>.Instance);

        await job.Execute(StubContext());

        recorder.LastSkipAuditWhenIdle.Should().BeTrue(
            "1 分钟周期空转必须走静默模式，否则 Audit_ScheduledTaskRun 每年膨胀约 50 万行且 Dashboard 时间轴被刷屏");
        recorder.LastContext!.Processed.Should().Be(0, "空转时 WithProcessed(0) 让 Recorder 判定 idle");
    }

    [Fact]
    public async Task AddWebhookRetryJob_RegistersJob_WithDefault1minSimpleTrigger()
    {
        IScheduler scheduler = await BuildScheduler(interval: null);

        IJobDetail? detail = await scheduler.GetJobDetail(WebhookRetryJob.Key);
        detail.Should().NotBeNull();
        detail!.JobType.Should().Be<WebhookRetryJob>();
        detail.Durable.Should().BeTrue();

        IReadOnlyCollection<ITrigger> triggers = await scheduler.GetTriggersOfJob(WebhookRetryJob.Key);
        triggers.Should().ContainSingle();
        SimpleTriggerImpl simple = triggers.Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        simple.RepeatInterval.Should().Be(TimeSpan.FromMinutes(1));
        simple.RepeatCount.Should().Be(SimpleTriggerImpl.RepeatIndefinitely);
        simple.MisfireInstruction.Should().Be(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount);
    }

    [Fact]
    public async Task AddWebhookRetryJob_Custom_Interval_Overrides_Default()
    {
        IScheduler scheduler = await BuildScheduler(interval: TimeSpan.FromSeconds(30));

        IReadOnlyCollection<ITrigger> triggers = await scheduler.GetTriggersOfJob(WebhookRetryJob.Key);
        SimpleTriggerImpl simple = triggers.Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        simple.RepeatInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    private static IJobExecutionContext StubContext(CancellationToken? token = null)
    {
        IJobExecutionContext ctx = Substitute.For<IJobExecutionContext>();
        ctx.FireInstanceId.Returns("test-fire-id");
        ctx.CancellationToken.Returns(token ?? CancellationToken.None);
        return ctx;
    }

    private static async Task<IScheduler> BuildScheduler(TimeSpan? interval)
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Substitute.For<IWebhookRetryCoordinator>());
        services.AddSingleton<IScheduledTaskRunRecorder>(new PassThroughTaskRunRecorder());
        services.AddQuartz(q =>
        {
            q.UseSimpleTypeLoader();
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 2);
            q.AddWebhookRetryJob(interval);
        });
        ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        return await factory.GetScheduler();
    }
}
