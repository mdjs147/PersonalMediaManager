using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using PersonalMediaManager.Host.Hubs;
using Serilog.Core;
using Serilog.Events;

namespace PersonalMediaManager.Host.Logging;

/// <summary>Serilog 自定义 Sink：把 Information+ 事件推到 LogHub；含 100 条/秒限流</summary>
/// <remarks>
/// 限流策略：固定 1 秒窗口令牌桶。窗口内累计 ≥100 条则丢弃；窗口结束自动重置；
/// 第一次进入「丢弃模式」时单发一条 Warning「日志推送限流」事件本身不计入计数。
/// 触发推送在后台线程：避免阻塞 Serilog 日志写入路径（File / Console）。
/// </remarks>
public sealed class SignalRSink : ILogEventSink
{
    private const int MaxEventsPerSecond = 100;
    private const string ThrottleEvent = "日志推送限流（前一秒丢弃 {0} 条）";

    private readonly IServiceProvider _serviceProvider;
    private readonly Lock _windowLock = new();
    private long _windowStartTicks;
    private int _windowCount;
    private int _droppedSinceLastWindow;
    private bool _throttleNoticeSent;

    public SignalRSink(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _windowStartTicks = DateTimeOffset.UtcNow.Ticks;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;

        if (!TryAcquireToken(out int dropped))
        {
            return;
        }

        // r2 P2-r2.4：消息文本与异常 message 走 SensitiveDataRedactor 脱敏（ApiKey / Bearer Token / JSON 敏感字段）
        // 推到前端 LogHub 的内容是潜在外泄面，强制在此处统一拦截，避免每个 logger 调用点都要记得脱敏
        LogEntry entry = new(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? ctx) ? ctx.ToString().Trim('"') : "",
            SensitiveDataRedactor.Redact(logEvent.RenderMessage()),
            SensitiveDataRedactor.Redact(logEvent.Exception?.Message));

        // 异步派发：失败不影响 Serilog 写其它 sink
        _ = PushAsync(entry);

        if (dropped > 0)
        {
            // 单发一条限流通知（带刚刚清零的丢弃数）
            LogEntry notice = new(
                DateTimeOffset.UtcNow,
                LogEventLevel.Warning.ToString(),
                nameof(SignalRSink),
                string.Format(ThrottleEvent, dropped),
                null);
            _ = PushAsync(notice);
        }
    }

    private async Task PushAsync(LogEntry entry)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            IHubContext<LogHub> hub = scope.ServiceProvider.GetRequiredService<IHubContext<LogHub>>();
            await hub.Clients.All.SendAsync("log", entry);
        }
        catch (Exception ex)
        {
            // r3 P3-r3.21：SignalR 推送失败必须留诊断信息但绝不能走 Serilog（会再触发本 Sink → 死循环）
            //   Serilog.Debugging.SelfLog 是 Serilog 自身的诊断旁路：默认 no-op，可在启动时由 Launcher / OPS 通过
            //   Serilog.Debugging.SelfLog.Enable(Console.Error) 等显式开启输出，写入不会再回到 Sink 管线。
            //   即使未启用 SelfLog，调用本身也是廉价 no-op，不影响主管线吞吐
            Serilog.Debugging.SelfLog.WriteLine(
                "SignalRSink 推送 LogHub 失败（Sink 内静默，建议 OPS 启用 Serilog.SelfLog 排查）：{0}",
                ex);
        }
    }

    private bool TryAcquireToken(out int droppedFromPreviousWindow)
    {
        droppedFromPreviousWindow = 0;
        long now = DateTimeOffset.UtcNow.Ticks;

        lock (_windowLock)
        {
            // 窗口跨过 1 秒 → 重置
            if (now - _windowStartTicks >= TimeSpan.TicksPerSecond)
            {
                if (_throttleNoticeSent)
                {
                    droppedFromPreviousWindow = _droppedSinceLastWindow;
                }
                _windowStartTicks = now;
                _windowCount = 0;
                _droppedSinceLastWindow = 0;
                _throttleNoticeSent = false;
            }

            if (_windowCount >= MaxEventsPerSecond)
            {
                _droppedSinceLastWindow++;
                _throttleNoticeSent = true;
                return false;
            }

            _windowCount++;
            return true;
        }
    }
}
