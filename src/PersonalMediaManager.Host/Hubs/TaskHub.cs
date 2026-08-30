using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PersonalMediaManager.Host.Hubs;

/// <summary>任务状态 SignalR Hub（D8.2）— /hubs/tasks</summary>
/// <remarks>
/// 客户端订阅 method：
///   taskStatusChanged  — 单 MediaItem 状态扭转事件（ProcessFileService Transition 后触发）
///   queueChanged       — 队列三档计数（Pending / Running / AwaitingReview）变化
/// 服务端走 IHubContext&lt;TaskHub&gt;.Clients.All.SendAsync 推送，不在 Hub 类内主动触发。
/// 推送语义：fire-and-forget；客户端断连/接收失败由 SignalR 自身重连补偿，业务侧不感知。
///
/// 鉴权：按需求文档 §3.12「匿名访问范围」明确列入「无需登录」（SignalR Hub 与仪表盘 / 队列 / 历史 / 日志同档）；
/// 类级 [AllowAnonymous] 让全局 FallbackPolicy(Admin) 放行未登录连接。
/// 推送内容仅含 MediaItemId / 状态名 / 队列计数，不含 ApiKey 或完整路径敏感片段。
///
/// r3 P3-r3.13：本 Hub **故意没有任何客户端可调用方法**（无 public 无 [HubMethodName]）—— 纯服务端推送模式
///   因此 JsonStringEnumConverter 对未知 enum 值降级到 0 的问题在 TaskHub 不存在攻击面；
///   未来若新增客户端可调方法且参数含 nullable enum，必须在方法入口显式 `if (arg is null) return BadRequest`
///   + 自定义 enum 校验（参考 ApiCode 三码原则在 Controller 层的做法）
/// </remarks>
[AllowAnonymous]
public sealed class TaskHub : Hub
{
    public const string Path = "/hubs/tasks";
}
