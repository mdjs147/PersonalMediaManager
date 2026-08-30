using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PersonalMediaManager.Host.Hubs;

/// <summary>日志 SignalR Hub（需求文档 §3.11）</summary>
/// <remarks>
/// 客户端通过 /hubs/logs 连接，订阅 method = "log"；服务端 SignalRSink 收到 Serilog 事件即 Clients.All.SendAsync("log", entry)。
/// 推送限流：单连接 100 条/秒（在 SignalRSink 内做；超出丢弃并发一条「日志推送限流」事件）。
/// 客户端按级别 / 关键词过滤（前端本地过滤；Hub 无服务端订阅参数）。
///
/// 鉴权：按需求文档 §3.12「匿名访问范围」明确列入「无需登录」（SignalR Hub 与仪表盘 / 队列 / 历史 / 日志同档）；
/// 类级 [AllowAnonymous] 让全局 FallbackPolicy(Admin) 放行未登录连接。
/// 敏感信息防护由 SensitiveDataRedactor 在 SignalRSink 内对 RenderMessage + Exception.Message 脱敏；
/// 产品定位已确认单管理员、不做多用户（2026-06-12 决策），[AllowAnonymous] + Clients.All 全量广播即终态设计，无需群组隔离。
///
/// r3 P3-r3.13：本 Hub **故意没有任何客户端可调用方法**（无 public 无 [HubMethodName]）—— 纯服务端推送模式
///   因此 JsonStringEnumConverter 对未知 enum 值降级到 0 的问题在 LogHub 不存在攻击面；
///   未来若新增客户端可调方法且参数含 nullable enum，必须在方法入口显式 `if (arg is null) return BadRequest`
///   + 自定义 enum 校验（参考 ApiCode 三码原则在 Controller 层的做法）
/// </remarks>
[AllowAnonymous]
public sealed class LogHub : Hub
{
    public const string Path = "/hubs/logs";
}
