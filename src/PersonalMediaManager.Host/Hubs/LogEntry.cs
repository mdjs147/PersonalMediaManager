namespace PersonalMediaManager.Host.Hubs;

/// <summary>SignalR 推送给前端日志页的最小 LogEntry DTO</summary>
/// <remarks>
/// 字段控制在前端日志列表渲染需要的最小集，避免 Serilog Property bag 完整序列化。
/// Exception 仅作 string（不暴露堆栈），前端默认隐藏，hover/点击展开。
/// </remarks>
public sealed record LogEntry(
    DateTimeOffset Time,
    string Level,
    string Category,
    string Message,
    string? Exception);
