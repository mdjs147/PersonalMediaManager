namespace PersonalMediaManager.Application.Dtos.Diagnostics;

/// <summary>Health 端点响应数据载荷（GET /health → data 字段）</summary>
/// <remarks>
/// 由 <c>HealthController.Get</c> 返回，固定为 <c>{ status: "ok" }</c>。
/// 端点用于 Launcher 启动后探活与浏览器跳转就绪判定；进程未起则返 502 / 无响应。
/// 抽到 Application 层是为了让 Swagger / Scalar 的 ProducesResponseType 注解与可复用 DTO 解耦于 Controller 文件。
/// </remarks>
public sealed record HealthStatusResponse(string Status);
