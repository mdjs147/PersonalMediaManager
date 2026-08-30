using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Dashboard;
using PersonalMediaManager.Application.Services.Dashboard;

namespace PersonalMediaManager.Host.Controllers.Dashboard;

/// <summary>Dashboard / 仪表盘</summary>
/// <remarks>
/// 首页只读：今日/总计/队列/解析来源/服务运行 + 最近处理列表。
/// 公开端点：无需登录（需求文档 §3.12 仪表盘对所有人可见）。
/// </remarks>
[ApiController]
[Route("dashboard")]
[AllowAnonymous]
public sealed class DashboardController : ApiControllerBase
{
    private readonly IDashboardService _service;
    private readonly IHealthCheckService _health;

    public DashboardController(IDashboardService service, IHealthCheckService health)
    {
        _service = service;
        _health = health;
    }

    /// <summary>Stats / 统计</summary>
    /// <remarks>
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "today":{...}, "total":{...}, "queue":{...}, "parseSource":{...}, "service":{...} }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — DB 查询失败
    /// </remarks>
    /// <response code="200">五维聚合统计</response>
    [HttpGet("stats")]
    [ProducesResponseType<ApiResponse<DashboardStats>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        DashboardStats data = await _service.GetStatsAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Health / 服务健康</summary>
    /// <remarks>
    /// 4 项并行 + 各项 3s 独立超时：Kestrel / SQLite / TMDB / AI 主提供商。
    /// 任一项超时 / 异常归 status=fail，整个 endpoint 永远 200 OK。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "checks":[ { "name":"Kestrel", "status":"ok", "detail":"上线 6d 14h", "latencyMs":null }, { "name":"SQLite (pmm.db)", "status":"ok", "detail":"24.6 MB", "latencyMs":null }, { "name":"TMDB", "status":"ok", "detail":"200 OK · 215ms", "latencyMs":215 }, { "name":"AI 主提供商", "status":"ok", "detail":"Qwen · 1240ms", "latencyMs":1240 } ] }, "requestId":"..." }
    /// ```
    /// 错误码：无（聚合实现侧吞咽所有异常）
    /// </remarks>
    /// <response code="200">健康检查聚合结果</response>
    [HttpGet("health")]
    [ProducesResponseType<ApiResponse<DashboardHealthResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        DashboardHealthResponse data = await _health.CheckAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Tasks / 任务历史</summary>
    /// <remarks>
    /// 查询参数：windowHours（默认 24，上限 168）/ limit（默认 50，上限 200）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "id":1, "jobKey":"scan.full-scan", "fireInstanceId":"...", "startedAt":"...", "finishedAt":"...", "durationMs":12400, "outcome":"Succeeded", "processedCount":18, "errorMessage":null, "detailJson":null } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — DB 查询失败
    /// </remarks>
    /// <response code="200">按 StartedAt 倒序的窗口内任务执行历史</response>
    [HttpGet("tasks")]
    [ProducesResponseType<ApiResponse<DashboardTasksResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tasks([FromQuery] int windowHours, [FromQuery] int limit, CancellationToken ct)
    {
        DashboardTasksResponse data = await _service.GetTasksAsync(windowHours, limit, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Heatmap / 活动热图</summary>
    /// <remarks>
    /// 查询参数：from / to（ISO8601）。缺省 from=now-7d, to=now；最长窗口 90 天，超出会自动收口到 to-90d。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "from":"...", "to":"...", "grid":[[0,0,1,2,0,...],[...]] }, "requestId":"..." }
    /// ```
    /// grid 是 7×24 二维数组（rows=DayOfWeek，0=Sun..6=Sat；cols=Hour 0..23 UTC）。
    /// 错误码：
    /// - 9000 ServerError — DB 查询失败
    /// </remarks>
    /// <response code="200">7×24 热图网格</response>
    [HttpGet("heatmap")]
    [ProducesResponseType<ApiResponse<DashboardHeatmapResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Heatmap(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        DashboardHeatmapResponse data = await _service.GetHeatmapAsync(from, to, ct);
        return Ok(Wrap(data));
    }

    /// <summary>WatchFolderActivity / 监控目录活动</summary>
    /// <remarks>
    /// 返回每监控目录最近 24h 归档 sparkline（每小时桶）。MediaItem ↔ WatchFolder 走 SourcePath 最长前缀匹配。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "folderId":1, "path":"F:/Downloads", "alias":"下载盘", "enabled":true, "total":12, "series":[0,0,1,0,...] } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — DB 查询失败
    /// </remarks>
    /// <response code="200">每目录 24 元素活动序列</response>
    [HttpGet("watch-folder-activity")]
    [ProducesResponseType<ApiResponse<DashboardWatchFolderActivityResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> WatchFolderActivity(CancellationToken ct)
    {
        DashboardWatchFolderActivityResponse data = await _service.GetWatchFolderActivityAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Recent / 最近处理</summary>
    /// <remarks>
    /// 查询参数：limit（默认 20，上限 100）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "id":1, "fileName":"...", "status":"Completed", "title":"...", "year":2010, "category":"电影", "targetPath":"...", "parseSource":"Rule", "archivedAt":"..." } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — DB 查询失败
    /// </remarks>
    /// <response code="200">按 UpdatedAt 倒序的最近 N 条</response>
    [HttpGet("recent")]
    [ProducesResponseType<ApiResponse<DashboardRecentList>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recent([FromQuery] int limit, CancellationToken ct)
    {
        DashboardRecentList data = await _service.GetRecentAsync(limit, ct);
        return Ok(Wrap(data));
    }
}
