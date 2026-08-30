using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;
using Quartz.Impl.Triggers;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Infrastructure.Platform.Scheduling;
using PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

namespace PersonalMediaManager.Application.Tests.Scheduling;

/// <summary>FullScanJob（D5.2）— Quartz Job 行为与触发器配置</summary>
/// <remarks>
/// 重点验证：
///   1. Execute 正常路径会调一次 IFullScanCoordinator.RunAsync
///   2. Coordinator 抛 OperationCanceledException 被吞（不抛出，避免污染 misfire 策略）
///   3. Coordinator 抛业务异常被吞但需走 Error 日志（不抛 JobExecutionException）
///   4. AddFullScanJob 默认注册 12h Trigger，自定义间隔生效
///   5. Job 标注 DisallowConcurrentExecution（防 12h 周期下重叠扫描）
/// </remarks>
public sealed class FullScanJobTests
{
    [Fact]
    public async Task Execute_CallsCoordinator_RunAsync_Once()
    {
        IFullScanCoordinator coordinator = Substitute.For<IFullScanCoordinator>();
        FullScanJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<FullScanJob>.Instance);
        IJobExecutionContext ctx = StubContext();

        await job.Execute(ctx);

        await coordinator.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_PassesCancellationToken_FromContext()
    {
        IFullScanCoordinator coordinator = Substitute.For<IFullScanCoordinator>();
        FullScanJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<FullScanJob>.Instance);
        using CancellationTokenSource cts = new();
        IJobExecutionContext ctx = StubContext(cts.Token);

        await job.Execute(ctx);

        await coordinator.Received(1).RunAsync(cts.Token);
    }

    [Fact]
    public async Task Execute_Swallows_OperationCanceled_WhenContextCancelled()
    {
        IFullScanCoordinator coordinator = Substitute.For<IFullScanCoordinator>();
        coordinator.RunAsync(Arg.Any<CancellationToken>()).Throws(new OperationCanceledException());
        FullScanJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<FullScanJob>.Instance);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        IJobExecutionContext ctx = StubContext(cts.Token);

        Func<Task> act = async () => await job.Execute(ctx);

        await act.Should().NotThrowAsync("被取消的扫描不应升级为 Job 失败");
    }

    [Fact]
    public async Task Execute_Swallows_BusinessException_AvoidsPollutingMisfirePolicy()
    {
        IFullScanCoordinator coordinator = Substitute.For<IFullScanCoordinator>();
        coordinator.RunAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("某文件夹不可访问"));
        FullScanJob job = new(coordinator, new PassThroughTaskRunRecorder(), NullLogger<FullScanJob>.Instance);
        IJobExecutionContext ctx = StubContext();

        Func<Task> act = async () => await job.Execute(ctx);

        await act.Should().NotThrowAsync(
            "FullScan 是 12h 周期的补偿任务，单次失败不需要走 Quartz misfire 重试");
        await coordinator.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Job_Has_DisallowConcurrentExecution_Attribute()
    {
        typeof(FullScanJob).GetCustomAttributes(typeof(DisallowConcurrentExecutionAttribute), inherit: false)
            .Should().NotBeEmpty("12h 周期下若上次未结束又被触发会产生扫描堆积");
    }

    [Fact]
    public async Task AddFullScanJob_RegistersJob_WithDefault12hSimpleTrigger()
    {
        IScheduler scheduler = await BuildSchedulerWithJob(interval: null);

        IJobDetail? detail = await scheduler.GetJobDetail(FullScanJob.Key);
        detail.Should().NotBeNull();
        detail!.JobType.Should().Be<FullScanJob>();
        detail.Durable.Should().BeTrue("StoreDurably 让 Job 在无 trigger 时不被清掉");

        IReadOnlyCollection<ITrigger> triggers = await scheduler.GetTriggersOfJob(FullScanJob.Key);
        triggers.Should().ContainSingle();
        SimpleTriggerImpl simple = triggers.Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        simple.RepeatInterval.Should().Be(TimeSpan.FromHours(12));
        simple.RepeatCount.Should().Be(SimpleTriggerImpl.RepeatIndefinitely);
        simple.MisfireInstruction.Should().Be(MisfireInstruction.SimpleTrigger.FireNow);
    }

    [Fact]
    public async Task AddFullScanJob_CustomInterval_OverridesDefault()
    {
        IScheduler scheduler = await BuildSchedulerWithJob(interval: TimeSpan.FromMinutes(15));

        IReadOnlyCollection<ITrigger> triggers = await scheduler.GetTriggersOfJob(FullScanJob.Key);
        SimpleTriggerImpl simple = triggers.Single().Should().BeOfType<SimpleTriggerImpl>().Subject;
        simple.RepeatInterval.Should().Be(TimeSpan.FromMinutes(15));
    }

    private static IJobExecutionContext StubContext(CancellationToken? token = null)
    {
        IJobExecutionContext ctx = Substitute.For<IJobExecutionContext>();
        ctx.FireInstanceId.Returns("test-fire-id");
        ctx.CancellationToken.Returns(token ?? CancellationToken.None);
        return ctx;
    }

    private static async Task<IScheduler> BuildSchedulerWithJob(TimeSpan? interval)
    {
        ServiceCollection services = new();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
                              typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSingleton(Substitute.For<IFullScanCoordinator>());
        services.AddSingleton<IScheduledTaskRunRecorder>(new PassThroughTaskRunRecorder());
        services.AddQuartz(q =>
        {
            q.UseSimpleTypeLoader();
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 2);
            q.AddFullScanJob(interval);
        });
        ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        return await factory.GetScheduler();
    }
}
