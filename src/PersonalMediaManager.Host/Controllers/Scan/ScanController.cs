using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Scan;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Controllers.Scan;

/// <summary>Scan / 扫描触发</summary>
/// <remarks>
/// 手动扫描入口；全量与单目录共享一个全局锁，并发触发 → 1000「已有扫描在进行中」。
/// 仅 Admin：显式标 [Authorize(Roles=Admin)] 表达意图（r1 P3-8），不再隐式依赖全局 FallbackPolicy；
/// 即使后续有人调整全局策略，本控制器仍坚持 Admin 限制。
/// </remarks>
[ApiController]
[Route("scan")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class ScanController : ApiControllerBase
{
    private readonly IScanService _service;
    private readonly IDryRunService _dryRun;

    public ScanController(IScanService service, IDryRunService dryRun)
    {
        _service = service;
        _dryRun = dryRun;
    }

    /// <summary>TriggerAll / 全量扫描</summary>
    /// <remarks>
    /// 请求体：空。
    /// 查询参数：
    /// - force（默认 false）：true 时遇已存在 MediaItem 自动重投 Failed 记录（Failed→Queued + ClearError + 入队），
    ///   Queued/Processing/Succeeded/Archived 仍跳过；false 与原行为一致（已存在一律跳过）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "scanId":"...", "folderCount":3, "filesEnqueued":42, "failedRescanned":5 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 已有扫描在进行中 / 未配置任何监控目录
    /// - 9000 ServerError — 触发扫描失败
    /// </remarks>
    /// <response code="200">已触发全量扫描</response>
    [HttpPost("trigger")]
    [ProducesResponseType<ApiResponse<ScanTriggerResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Trigger([FromQuery] bool force, CancellationToken ct)
    {
        ScanTriggerResult data = await _service.TriggerAllAsync(force, ct);
        return Ok(Wrap(data));
    }

    /// <summary>ScanFolder / 单目录</summary>
    /// <remarks>
    /// 请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "scanId":"...", "folderId":1, "path":"F:/Downloads", "filesEnqueued":10 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 监控目录不存在 / 目录已禁用 / 目录不可达 / 已有扫描在进行中
    /// </remarks>
    /// <response code="200">已触发单目录扫描</response>
    [HttpPost("folder/{id:long}")]
    [ProducesResponseType<ApiResponse<ScanFolderResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ScanFolder([FromRoute] long id, CancellationToken ct)
    {
        ScanFolderResult data = await _service.ScanFolderAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>ScanPath / 手动整理</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "path": "F:/Downloads/movie.mkv" }
    /// ```
    /// 路径可为单个视频文件或目录（目录递归枚举视频文件）；不必是已配置的监控目录。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "scanId":"...", "path":"F:/Downloads", "isDirectory":true, "filesEnqueued":3, "skipped":1 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 路径为空 / 路径不存在 / 不是受支持的视频文件 / 已有扫描在进行中
    /// - 9000 ServerError — 手动整理失败
    /// </remarks>
    /// <response code="200">已入队手动整理</response>
    [HttpPost("path")]
    [ProducesResponseType<ApiResponse<ScanPathResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ScanPath([FromBody] ScanPathRequest request, CancellationToken ct)
    {
        ScanPathResult data = await _service.ScanPathAsync(request.Path, ct);
        return Ok(Wrap(data));
    }

    /// <summary>DryRun / 整理演练</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "path": "F:/Downloads/盗梦空间.2010.1080p.mkv" }
    /// ```
    /// 只读演练：规则解析 + TMDB 匹配 + Plex 命名预览，**不调用 AI、不移动文件、不写处理记录**。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "path":"...", "fileName":"...", "title":"盗梦空间", "year":2010, "mediaType":"movie", "outcome":"WouldArchive", "outcomeReason":"...", "tmdbQueried":true, "candidates":[], "picked":{}, "previewRelativePath":"盗梦空间 (2010) {tmdb-27205}/盗梦空间 (2010) {tmdb-27205}.mkv", "previewNote":"..." }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 路径为空 / 无法解析文件名
    /// - 9000 ServerError — 演练失败
    /// </remarks>
    /// <response code="200">演练完成</response>
    [HttpPost("dry-run")]
    [ProducesResponseType<ApiResponse<DryRunPreview>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DryRun([FromBody] DryRunRequest request, CancellationToken ct)
    {
        DryRunPreview data = await _dryRun.PreviewAsync(request.Path, ct);
        return Ok(Wrap(data));
    }
}
