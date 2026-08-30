using PersonalMediaManager.Application.Services.Audit;

namespace PersonalMediaManager.Application.Tests.Scheduling;

/// <summary>测试用 IScheduledTaskRunRecorder：直接执行 body，复刻真实 Recorder 的异常吞咽语义</summary>
/// <remarks>
/// 真实实现 ScheduledTaskRunRecorder 把异常吞咽落到 Audit_ScheduledTaskRun.Outcome=Failed；
/// 测试侧无需关心持久化结果，但需要保留「OperationCanceled / 业务异常都不向外抛」这一不变式，
/// 否则 Quartz Job 测试用例（Execute_Swallows_*）会失败。
/// LastContext / LastSkipAuditWhenIdle 捕获最近一次调用，供 Job 测试断言 With* 链与空转静默开关。
/// </remarks>
internal sealed class PassThroughTaskRunRecorder : IScheduledTaskRunRecorder
{
    /// <summary>最近一次 RunAsync 传给 body 的 ctx（断言 Processed / Detail 用）</summary>
    public ScheduledTaskRunContext? LastContext { get; private set; }

    /// <summary>最近一次 RunAsync 的 skipAuditWhenIdle 实参（断言空转静默开关用）</summary>
    public bool? LastSkipAuditWhenIdle { get; private set; }

    public async Task RunAsync(
        string jobKey,
        string? fireInstanceId,
        Func<ScheduledTaskRunContext, Task> body,
        CancellationToken ct,
        bool skipAuditWhenIdle = false)
    {
        ScheduledTaskRunContext ctx = new(ct);
        LastContext = ctx;
        LastSkipAuditWhenIdle = skipAuditWhenIdle;
        try
        {
            await body(ctx);
        }
        catch
        {
            // 与真实 Recorder 一致：取消 + 业务异常均吞咽（真实实现写 Outcome=Canceled/Failed + 记日志，本测试桩仅吞）
        }
    }
}
