using PersonalMediaManager.Application.Dtos.Dashboard;

namespace PersonalMediaManager.Application.Services.Dashboard;

/// <summary>仪表盘健康检查聚合（4 项并行 + 各项独立超时）</summary>
/// <remarks>
/// 实现责任：4 项检查并行执行，单项独立 3s 超时；任一项异常 / 超时不影响其他项；
/// 单项异常归类到 status=fail + detail 写错误摘要，整个 endpoint 永远 200 OK。
/// </remarks>
public interface IHealthCheckService
{
    Task<DashboardHealthResponse> CheckAsync(CancellationToken ct = default);
}
