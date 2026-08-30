using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.History;
using PersonalMediaManager.Application.Services.History;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Host.Controllers.History;

/// <summary>History / 处理历史</summary>
/// <remarks>
/// 已归档 / 已跳过 / 已忽略 / 已取消 / 失败记录的回看 + 失败重投。
/// GET 公开（只读复盘），rescan 走 Admin（写入 + 触发管线）。
/// rescan 仅 Failed 允许，其它状态对外回"任务正在处理中，请稍后重试"。
/// </remarks>
[ApiController]
[Route("history")]
public sealed class HistoryController : ApiControllerBase
{
    private readonly IHistoryService _service;

    public HistoryController(IHistoryService service) { _service = service; }

    /// <summary>List / 历史列表</summary>
    /// <remarks>
    /// 查询参数：page / pageSize（≤100）/ status / parseSource / categoryId / from / to / keyword。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "id":123, "fileName":"...", "title":"盗梦空间", "year":2010, "status":"Completed", "parseSource":"Rule", "confidence":0.95, "category":"电影", "targetPath":"...", "archivedAt":"...", "updatedAt":"...", "metadataPending":false } ], "total":1058, "page":1, "pageSize":20 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 分页参数非法 / 时间区间格式错误
    /// </remarks>
    /// <response code="200">按 UpdatedAt 倒序的分页结果</response>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<HistoryListPage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] HistoryListQuery query, CancellationToken ct)
    {
        HistoryListPage data = await _service.ListAsync(query, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Pending / 处理队列</summary>
    /// <remarks>
    /// 列出全部在途任务（排队 + 处理中，不含待确认与终态），按 Id 正序（先进先出）；与 Dashboard「队列长度」同口径。
    /// 查询参数：page / pageSize（≤100）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "id":456, "fileName":"...", "title":"繁花", "year":2023, "status":"Parsing", "parseSource":null, "fileSize":1572864000, "updatedAt":"..." } ], "total":12, "page":1, "pageSize":20 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 分页参数非法
    /// </remarks>
    /// <response code="200">按 Id 正序的在途任务分页结果</response>
    [HttpGet("pending")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<HistoryListPage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pending([FromQuery] PendingListQuery query, CancellationToken ct)
    {
        HistoryListPage data = await _service.ListPendingAsync(query, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Detail / 历史详情</summary>
    /// <remarks>
    /// Path：{id} 媒体记录 ID。返回 MediaItem 全字段 + 关联 TmdbMetadataCache + Audit_AiCall 列表。
    /// 候选集（TmdbCandidates）实时计算未持久化，故详情仅返回最终命中的元数据。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":123, "fileName":"...", "status":"Completed", "tmdb":{ "tmdbId":200000, "title":"繁花" }, "aiCalls":[ {"id":1,"providerId":2,"success":true,"latencyMs":1240,"timestamp":"..."} ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在
    /// </remarks>
    /// <response code="200">单条详情</response>
    [HttpGet("{id:long}")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<MediaItemDetailResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail([FromRoute] long id, CancellationToken ct)
    {
        MediaItemDetailResponse data = await _service.GetDetailAsync(id, ct);
        // 匿名 / 非 Admin 只读复盘：把完整绝对路径脱敏为文件名，避免泄露盘符 / 用户目录结构（源路径常含下载目录与用户名）
        if (!User.IsInRole(UserRole.Admin.ToString()))
            data = data with
            {
                SourcePath = Path.GetFileName(data.SourcePath),
                TargetPath = string.IsNullOrEmpty(data.TargetPath) ? data.TargetPath : Path.GetFileName(data.TargetPath),
            };
        return Ok(Wrap(data));
    }

    /// <summary>Cancel / 取消排队或归档前正在处理的任务</summary>
    /// <remarks>
    /// 排队任务直接落 Cancelled，之后从内存队列出队时按终态跳过；正在处理任务先取消独立处理令牌，
    /// 等管线安全退出后再落 Cancelled。Archiving 已可能移动文件，不允许取消；若请求到达前任务已结束也拒绝，
    /// 避免把真实磁盘结果覆盖成取消。
    /// </remarks>
    /// <response code="200">任务已取消</response>
    [HttpPost("{id:long}/cancel")]
    [ProducesResponseType<ApiResponse<CancelTaskResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel([FromRoute] long id, CancellationToken ct)
    {
        CancelTaskResult data = await _service.CancelAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Nfo / 下载元数据</summary>
    /// <remarks>
    /// 流式返回已归档媒体记录目录下的 .nfo 文件（Path.ChangeExtension(TargetPath, ".nfo")）。
    /// 仅 Admin（写文件无关但同 rescan 等价管控位）。
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 该记录尚未归档 / .nfo 文件不存在
    /// </remarks>
    /// <response code="200">.nfo 字节流（application/xml; attachment）</response>
    [HttpGet("{id:long}/nfo")]
    [Produces("application/xml")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Nfo([FromRoute] long id, CancellationToken ct)
    {
        NfoDownloadResult data = await _service.GetNfoAsync(id, ct);
        return File(data.Content, data.ContentType, data.FileName);
    }

    /// <summary>Poster / 海报图</summary>
    /// <remarks>
    /// 流式返回该记录 TMDB 海报的本地缓存（AppPaths.PostersDir/{TmdbId}.jpg）。
    /// 匿名：&lt;img&gt; 标签无法携带 Authorization 头，且海报为公开 TMDB 图无敏感性。
    /// 未缓存 / 无 TmdbId / 记录不存在 → 404，前端 &lt;img onerror&gt; 降级到占位图。
    /// </remarks>
    /// <response code="200">海报字节流（image/jpeg）</response>
    /// <response code="404">无缓存海报</response>
    [HttpGet("{id:long}/poster")]
    [AllowAnonymous]
    [Produces("image/jpeg")]
    [ProducesResponseType(typeof(PhysicalFileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Poster([FromRoute] long id, CancellationToken ct)
    {
        string? path = await _service.GetPosterPathAsync(id, ct);
        if (path is null) return NotFound();
        return PhysicalFile(path, "image/jpeg");
    }

    /// <summary>Rescan / 重投</summary>
    /// <remarks>
    /// 请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":123, "status":"Queued" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 任务正在处理中 / 源文件已不存在
    /// - 9000 ServerError — 重投任务失败
    /// </remarks>
    /// <response code="200">转 Queued 并重入主管线</response>
    [HttpPost("{id:long}/rescan")]
    [ProducesResponseType<ApiResponse<RescanResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rescan([FromRoute] long id, CancellationToken ct)
    {
        RescanResult data = await _service.RescanAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>TestArchive / 测试路径</summary>
    /// <remarks>
    /// 请求体：空。只读演算该记录的归档去向并逐项体检（源文件 / 分类 / TMDB / 字段 / Plex 命名 / 路径穿越 / 同名冲突），
    /// **不动文件、不写库**；专治「最后一部 / 越界集号复制提示路径错误」——精确暴露命名报错成因。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":123, "status":"Failed", "sourcePath":"...", "sourceExists":true, "targetRoot":"D:/Media/剧集", "isTv":true, "title":"繁花", "year":2023, "season":1, "episode":30, "episodeEnd":null, "relativePath":null, "targetPath":null, "targetExists":false, "canArchive":false, "error":"Plex 命名校验未通过：EpisodeEnd 必须 >= Episode", "checks":[ {"name":"Plex 命名校验","ok":false,"detail":"..."} ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"记录不存在", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">归档去向体检结果（error 与 targetPath 互斥）</response>
    [HttpPost("{id:long}/preview-archive")]
    [ProducesResponseType<ApiResponse<ArchivePreviewResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewArchive([FromRoute] long id, CancellationToken ct)
    {
        ArchivePreviewResult data = await _service.PreviewArchiveAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>ManualArchive / 手动移动</summary>
    /// <remarks>
    /// 用记录现有元数据直接重跑归档（**不重新解析**），把源文件移动 / 复制到 Plex 目标路径。
    /// 仅 Failed / Skipped / AwaitingReview 记录可用；区别于「重新处理」（重投整条解析管线）。
    /// 请求体：
    /// ```json
    /// { "mode": "Move" }
    /// ```
    /// mode：Move（移动，删源）/ Copy（复制，保留源，NAS / 网盘场景），缺省 Move。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":123, "status":"Completed", "targetPath":"D:/Media/剧集/繁花 (2023) {tmdb-...}/Season 01/繁花 (2023) - S01E30.mkv", "outcome":"Completed", "warnings":null }, "requestId":"..." }
    /// ```
    /// warnings：视频已落地但部分元数据（nfo / 海报 / Webhook）写入失败时的中文警告列表，非空表示「待补元数据」；正常归档为 null。
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 状态不支持手动移动 / 缺分类或 TMDB 或解析信息 / 源文件不存在 / 未知模式 / 手动移动失败
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"源文件已不存在，无法手动移动", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">已落地，返回目标路径与最终状态</response>
    [HttpPost("{id:long}/manual-archive")]
    [ProducesResponseType<ApiResponse<ManualArchiveResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ManualArchive([FromRoute] long id, [FromBody] ManualArchiveRequest req, CancellationToken ct)
    {
        ManualArchiveResult data = await _service.ManualArchiveAsync(id, req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>UndoArchive / 撤销归档</summary>
    /// <remarks>
    /// 把已完成归档的文件移回源位置（当初 Copy 且源仍在则删归档副本），记录回退为 Skipped（未入库）。
    /// 仅 Completed 记录可撤销；选 Skipped 而非重投队列，避免按原规则立刻重新归档回同一错误位置。
    /// 请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":123, "status":"Skipped", "restoredPath":"F:/下载/繁花.S01E01.mkv", "operation":"MOVE_BACK" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 非已完成状态 / 缺归档路径 / 归档文件已不存在 / 源位被占用 / 撤销失败
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"归档文件已不存在（可能被外部移动 / 删除 / 改名），无法撤销", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">已撤销，返回回退去向与动作类型</response>
    [HttpPost("{id:long}/undo-archive")]
    [ProducesResponseType<ApiResponse<UndoArchiveResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UndoArchive([FromRoute] long id, CancellationToken ct)
    {
        UndoArchiveResult data = await _service.UndoArchiveAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Reopen / 退回重改</summary>
    /// <remarks>
    /// 把已确认归档（Completed/Skipped）的记录退回「待人工确认」队列重新填资料：
    /// Completed 先把归档文件移回源位（同撤销归档），Skipped 文件本就在源位不动；记录回到 AwaitingReview。
    /// 请求体：空。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":123, "status":"AwaitingReview", "restoredPath":"F:/下载/繁花.S01E01.mkv", "operation":"MOVE_BACK" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 状态不支持退回 / 缺归档路径 / 文件已不存在 / 源位被占用 / 退回失败
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"仅「已完成」或「已跳过」记录可退回重改", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">已退回待确认队列，返回源位置与动作类型</response>
    [HttpPost("{id:long}/reopen")]
    [ProducesResponseType<ApiResponse<ReopenResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reopen([FromRoute] long id, CancellationToken ct)
    {
        ReopenResult data = await _service.ReopenForReviewAsync(id, ct);
        return Ok(Wrap(data));
    }

    /// <summary>BatchReopen / 批量退回</summary>
    /// <remarks>
    /// 把多条已确认归档的记录批量退回「待人工确认」队列重新填资料（逐条移回源位 + 回 AwaitingReview）；
    /// 选择器同重新处理：单条 / 批量 Ids，或整剧 TmdbId 展开。仅 Completed/Skipped 可退回，其余落入 failed。
    /// 请求体：
    /// ```json
    /// { "ids":[123,124], "tmdbId":null, "tmdbMediaType":null }
    /// ```
    /// 成功响应（部分失败也返 200，明细见 failed）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":2, "succeeded":[123], "failed":[ {"id":124,"message":"仅「已完成」或「已跳过」记录可退回重改"} ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未指定要操作的记录 / 超出单次上限 / 该剧集下没有可操作的记录
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"未指定要操作的记录", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">批量退回，返回成功 / 失败明细</response>
    [HttpPost("batch-reopen")]
    [ProducesResponseType<ApiResponse<HistoryBatchResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BatchReopen([FromBody] HistoryBatchRequest req, CancellationToken ct)
    {
        HistoryBatchResult data = await _service.BatchReopenAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>BatchUndoArchive / 批量撤销</summary>
    /// <remarks>
    /// 把多条已完成归档的记录批量撤销（逐条移回源位 / 删归档副本 + 回 Skipped）；
    /// 选择器同重新处理：单条 / 批量 Ids，或整剧 TmdbId 展开。仅 Completed 可撤销，其余落入 failed。
    /// 请求体：
    /// ```json
    /// { "ids":[123,124], "tmdbId":null, "tmdbMediaType":null }
    /// ```
    /// 成功响应（部分失败也返 200，明细见 failed）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":2, "succeeded":[123,124], "failed":[] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未指定要操作的记录 / 超出单次上限 / 该剧集下没有可操作的记录
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"未指定要操作的记录", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">批量撤销，返回成功 / 失败明细</response>
    [HttpPost("batch-undo-archive")]
    [ProducesResponseType<ApiResponse<HistoryBatchResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BatchUndoArchive([FromBody] HistoryBatchRequest req, CancellationToken ct)
    {
        HistoryBatchResult data = await _service.BatchUndoArchiveAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>RescanFailed / 批量重试</summary>
    /// <remarks>
    /// 请求体：空。把所有 Failed 记录重置回 Queued 并重入主管线；源文件已不存在的跳过。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "requeued":12, "skipped":2 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — 批量重试失败
    /// </remarks>
    /// <response code="200">已批量重投失败记录</response>
    [HttpPost("rescan-failed")]
    [ProducesResponseType<ApiResponse<BatchRescanResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RescanFailed(CancellationToken ct)
    {
        BatchRescanResult data = await _service.RescanAllFailedAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>Reprocess / 重新处理</summary>
    /// <remarks>
    /// 删旧记录后以全新检测重投源文件，跑一次彻底全新的解析归档；仅终态记录可重新处理、源文件须仍存在。
    /// 支持单条 / 批量 / 整剧：Ids 优先；Ids 空时按 TmdbId 展开该剧全部终态记录。
    /// 请求体：
    /// ```json
    /// { "ids":[123,124], "tmdbId":null, "tmdbMediaType":null }
    /// ```
    /// 整剧示例：`{ "ids":null, "tmdbId":120089, "tmdbMediaType":"tv" }`
    /// 成功响应（部分失败也返 200，明细见 failed）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":2, "succeeded":[123], "failed":[ {"id":124,"message":"源文件已不存在，无法重新处理"} ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未指定要操作的记录 / 超出单次上限 / 该剧集下没有可操作的记录
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"未指定要操作的记录", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">删旧记录并重投，返回成功 / 失败明细</response>
    [HttpPost("reprocess")]
    [ProducesResponseType<ApiResponse<HistoryBatchResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reprocess([FromBody] HistoryBatchRequest req, CancellationToken ct)
    {
        HistoryBatchResult data = await _service.ReprocessAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Delete / 删除记录</summary>
    /// <remarks>
    /// 删除历史记录（处理时间线级联删、AI 调用统计解绑保留）；仅终态记录可删，在途 / 待确认拒绝。
    /// 支持单条 / 批量 / 整剧：Ids 优先；Ids 空时按 TmdbId 展开该剧全部终态记录。
    /// 请求体：
    /// ```json
    /// { "ids":[123,124], "tmdbId":null, "tmdbMediaType":null }
    /// ```
    /// 成功响应（部分失败也返 200，明细见 failed）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":2, "succeeded":[123,124], "failed":[] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未指定要操作的记录 / 超出单次上限 / 该剧集下没有可操作的记录
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"记录正在处理或待确认中，暂不能删除", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">删除记录，返回成功 / 失败明细</response>
    [HttpPost("delete")]
    [ProducesResponseType<ApiResponse<HistoryBatchResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete([FromBody] HistoryBatchRequest req, CancellationToken ct)
    {
        HistoryBatchResult data = await _service.DeleteAsync(req, ct);
        return Ok(Wrap(data));
    }
}
