using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl.Triggers;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Infrastructure.Platform.Scheduling;
using PersonalMediaManager.Infrastructure.Platform.Scheduling.Jobs;

namespace PersonalMediaManager.Application.Tests.Scheduling;

/// <summary>LogRetentionJob（D5.3）— 30 天保留 + 通配 + 容错</summary>
/// <remarks>
/// 关键不变式：
///   1. 仅删 pmm-*.log，绝不动其它扩展名 / 其它命名的文件
///   2. 删除依据 LastWriteTimeUtc（不解析文件名，兼容 10MB 切割产生的 _NNN 文件）
///   3. 截止时刻边界：&lt; cutoff 删，= cutoff 保留（按 IClock.UtcNow - 30d 计算）
///   4. 目录不存在不抛
///   5. 单文件删除失败（占用 / 权限）不阻断其他文件
///   6. Cron 默认每天一次
/// </remarks>
public sealed class LogRetentionJobTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public LogRetentionJobTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pmm-logret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _paths = AppPaths.ForRoot(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PurgeOlderThan_Deletes_File_Older_Than_Retention()
    {
        DateTimeOffset now = new(2026, 5, 17, 3, 0, 0, TimeSpan.Zero);
        string oldFile = WriteLog("pmm-20260301.log", lastWriteUtc: now.AddDays(-31).UtcDateTime);

        LogRetentionJob job = NewJob(now);

        int deleted = job.PurgeOlderThan(_paths.LogDir, TimeSpan.FromDays(30));

        deleted.Should().Be(1);
        File.Exists(oldFile).Should().BeFalse();
    }

    [Fact]
    public void PurgeOlderThan_Keeps_File_Newer_Than_Retention()
    {
        DateTimeOffset now = new(2026, 5, 17, 3, 0, 0, TimeSpan.Zero);
        string freshFile = WriteLog("pmm-20260510.log", lastWriteUtc: now.AddDays(-7).UtcDateTime);

        LogRetentionJob job = NewJob(now);

        int deleted = job.PurgeOlderThan(_paths.LogDir, TimeSpan.FromDays(30));

        deleted.Should().Be(0);
        File.Exists(freshFile).Should().BeTrue();
    }

    [Fact]
    public void PurgeOlderThan_File_AtExact_Cutoff_IsKept()
    {
        DateTimeOffset now = new(2026, 5, 17, 3, 0, 0, TimeSpan.Zero);
        DateTime cutoffUtc = now.AddDays(-30).UtcDateTime;
        string borderFile = WriteLog("pmm-20260417.log", lastWriteUtc: cutoffUtc);

        LogRetentionJob job = NewJob(now);

        int deleted = job.PurgeOlderThan(_paths.LogDir, TimeSpan.FromDays(30));

        deleted.Should().Be(0, "边界文件 LastWriteTimeUtc == cutoff 应当保留");
        File.Exists(borderFile).Should().BeTrue();
    }

    [Fact]
    public void PurgeOlderThan_Ignores_NonPmm_Files()
    {
        DateTimeOffset now = new(2026, 5, 17, 3, 0, 0, TimeSpan.Zero);
        string other = WriteLog("nginx-access.log", lastWriteUtc: now.AddDays(-100).UtcDateTime);
        string notLog = WriteLog("pmm-notes.txt", lastWriteUtc: now.AddDays(-100).UtcDateTime);

        LogRetentionJob job = NewJob(now);

        int deleted = job.PurgeOlderThan(_paths.LogDir, TimeSpan.FromDays(30));

        deleted.Should().Be(0);
        File.Exists(other).Should().BeTrue();
        File.Exists(notLog).Should().BeTrue();
    }

    [Fact]
    public void PurgeOlderThan_Deletes_Rolled_Size_Cut_File()
    {
        DateTimeOffset now = new(2026, 5, 17, 3, 0, 0, TimeSpan.Zero);
        string rolled = WriteLog("pmm-20260301_001.log", lastWriteUtc: now.AddDays(-31).UtcDateTime);

        LogRetentionJob job = NewJob(now);

        int deleted = job.PurgeOlderThan(_paths.LogDir, TimeSpan.FromDays(30));

        deleted.Should().Be(1, "Serilog 10MB 切割产物 pmm-*_NNN.log 同样应被清理");
        File.Exists(rolled).Should().BeFalse();
    }

    [Fact]
    public void PurgeOlderThan_Missing_LogDir_Returns_Zero()
    {
        string ghost = Path.Combine(_root, "no-such-dir");
        LogRetentionJob job = NewJob(DateTimeOffset.UtcNow);

        int deleted = job.PurgeOlderThan(ghost, TimeSpan.FromDays(30));

        deleted.Should().Be(0);
    }

    [Fact]
    public void PurgeOlderThan_Locked_File_Skipped_Other_Files_Still_Deleted()
    {
        DateTimeOffset now = new(2026, 5, 17, 3, 0, 0, TimeSpan.Zero);
        string locked = WriteLog("pmm-20260301.log", lastWriteUtc: now.AddDays(-31).UtcDateTime);
        string alsoOld = WriteLog("pmm-20260302.log", lastWriteUtc: now.AddDays(-31).UtcDateTime);

        using FileStream _ = new(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        LogRetentionJob job = NewJob(now);

        int deleted = job.PurgeOlderThan(_paths.LogDir, TimeSpan.FromDays(30));

        deleted.Should().Be(1, "被占用文件跳过，其他过期文件照删");
        File.Exists(locked).Should().BeTrue();
        File.Exists(alsoOld).Should().BeFalse();
    }

    [Fact]
    public async Task Execute_Swallows_Exception_From_Coordinator()
    {
        // 不可达路径让 EnumerateFiles 抛
        AppPaths ghostPaths = AppPaths.ForRoot(Path.Combine(_root, "ghost"));
        // 删 LogDir 之后再调，Execute 应当走「Directory.Exists==false 返回 0」分支，不抛
        Directory.Delete(ghostPaths.LogDir);
        LogRetentionJob job = new(ghostPaths, new FakeClock(DateTimeOffset.UtcNow), Substitute.For<IScheduledTaskRunRetentionService>(), new PassThroughTaskRunRecorder(), NullLogger<LogRetentionJob>.Instance);
        IJobExecutionContext ctx = Substitute.For<IJobExecutionContext>();
        ctx.FireInstanceId.Returns("test-fire");

        Func<Task> act = async () => await job.Execute(ctx);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Execute_Purges_TaskRuns_And_Reports_Combined_Processed_With_Detail()
    {
        // 1 个超期日志文件 + retention 服务报告删了 7 行调度记录 → processed = 1+7，detail 分项可追溯
        DateTimeOffset now = new(2026, 6, 12, 3, 0, 0, TimeSpan.Zero);
        WriteLog("pmm-20260301.log", lastWriteUtc: now.AddDays(-31).UtcDateTime);
        IScheduledTaskRunRetentionService retention = Substitute.For<IScheduledTaskRunRetentionService>();
        retention.PurgeAsync(Arg.Any<CancellationToken>()).Returns(7);
        PassThroughTaskRunRecorder recorder = new();
        LogRetentionJob job = new(_paths, new FakeClock(now), retention, recorder, NullLogger<LogRetentionJob>.Instance);
        IJobExecutionContext ctx = Substitute.For<IJobExecutionContext>();
        ctx.FireInstanceId.Returns("test-fire");

        await job.Execute(ctx);

        await retention.Received(1).PurgeAsync(Arg.Any<CancellationToken>());
        recorder.LastContext.Should().NotBeNull();
        recorder.LastContext!.Processed.Should().Be(8, "processed = 删除日志文件数(1) + 删除调度记录行数(7)");
        recorder.LastContext.Detail.Should().Be("""{"logFiles":1,"taskRuns":7}""");
    }

    [Fact]
    public async Task AddLogRetentionJob_Registers_Daily_Cron_3AM()
    {
        IScheduler scheduler = await BuildScheduler(cron: null);

        IJobDetail? detail = await scheduler.GetJobDetail(LogRetentionJob.Key);
        detail.Should().NotBeNull();
        detail!.JobType.Should().Be<LogRetentionJob>();

        IReadOnlyCollection<ITrigger> triggers = await scheduler.GetTriggersOfJob(LogRetentionJob.Key);
        triggers.Should().ContainSingle();
        CronTriggerImpl cron = triggers.Single().Should().BeOfType<CronTriggerImpl>().Subject;
        cron.CronExpressionString.Should().Be(PmmJobsRegistration.DefaultLogRetentionCron);
    }

    [Fact]
    public async Task AddLogRetentionJob_Custom_Cron_Overrides_Default()
    {
        IScheduler scheduler = await BuildScheduler(cron: "0 30 4 * * ?");

        IReadOnlyCollection<ITrigger> triggers = await scheduler.GetTriggersOfJob(LogRetentionJob.Key);
        CronTriggerImpl cron = triggers.Single().Should().BeOfType<CronTriggerImpl>().Subject;
        cron.CronExpressionString.Should().Be("0 30 4 * * ?");
    }

    [Fact]
    public void Job_Has_DisallowConcurrentExecution_Attribute()
    {
        typeof(LogRetentionJob).GetCustomAttributes(typeof(DisallowConcurrentExecutionAttribute), inherit: false)
            .Should().NotBeEmpty();
    }

    private LogRetentionJob NewJob(DateTimeOffset now)
        => new(_paths, new FakeClock(now), Substitute.For<IScheduledTaskRunRetentionService>(), new PassThroughTaskRunRecorder(), NullLogger<LogRetentionJob>.Instance);

    private string WriteLog(string fileName, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_paths.LogDir, fileName);
        File.WriteAllText(path, "log");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private async Task<IScheduler> BuildScheduler(string? cron)
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_paths);
        services.AddSingleton<IClock>(new FakeClock(DateTimeOffset.UtcNow));
        services.AddSingleton(Substitute.For<IScheduledTaskRunRetentionService>());
        services.AddSingleton<IScheduledTaskRunRecorder>(new PassThroughTaskRunRecorder());
        services.AddQuartz(q =>
        {
            q.UseSimpleTypeLoader();
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 2);
            q.AddLogRetentionJob(cron);
        });
        ServiceProvider sp = services.BuildServiceProvider();
        ISchedulerFactory factory = sp.GetRequiredService<ISchedulerFactory>();
        return await factory.GetScheduler();
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; }
    }
}
