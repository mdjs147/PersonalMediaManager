using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Review;
using PersonalMediaManager.Application.Services.Review;

namespace PersonalMediaManager.Host.Controllers.Review;

/// <summary>Review / 人工确认队列</summary>
/// <remarks>
/// AwaitingReview 状态记录的人工兜底入口：list / confirm / ignore / batch-confirm / tmdb-search / bind-tmdb。
/// 鉴权遵循需求文档 §3.12「匿名访问范围」——「待处理队列（看）」匿名可见；TmdbSearch / TmdbDetail 代理外部 TMDB（耗 ApiKey 额度），收紧为需登录防匿名刷额度：
///   - GET List / PreviewPaths（纯本地只读）显式 [AllowAnonymous]
///   - GET TmdbSearch / TmdbDetail（代理 TMDB）与所有写操作走全局 FallbackPolicy(Admin)
/// 所有写操作走乐观并发 RowVersion，冲突 → 1000「记录已被其他用户修改，请刷新」。
/// </remarks>
[ApiController]
[Route("review")]
public sealed class ReviewController : ApiControllerBase
{
    private readonly IReviewService _service;

    public ReviewController(IReviewService service) { _service = service; }

    /// <summary>List / 待确认列表</summary>
    /// <remarks>
    /// 查询参数：page / pageSize（≤100）/ parseSource（Rule/Ai/Hybrid）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "id":200, "fileName":"...", "sourcePath":"...", "parseSource":"AI", "confidence":0.42, "parsedInfo":"...", "tmdbCandidates":[ { "tmdbId":27205, "title":"盗梦空间", "year":2010, "posterUrl":"..." } ], "rowVersion":3, "createdAt":"..." } ], "total":1, "page":1, "pageSize":20 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — page &lt; 1
    /// </remarks>
    /// <response code="200">分页列表</response>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<ReviewListPage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ReviewListQuery query, CancellationToken ct)
    {
        ReviewListPage data = await _service.ListAsync(query, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Confirm / 确认归档</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "tmdbId":27205, "mediaType":"movie", "categoryId":1, "title":"盗梦空间", "year":2010, "season":null, "episode":null, "rowVersion":3 }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":200, "status":"Archiving", "rowVersion":4 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 并发冲突 / 分类不存在 / 剧集缺季集 / TMDB ID 无效 / mediaType 非法 / 该记录不在待确认状态
    /// - 9000 ServerError — 提交归档任务失败
    /// </remarks>
    /// <response code="200">转 Archiving 成功</response>
    [HttpPost("{id:long}/confirm")]
    [ProducesResponseType<ApiResponse<ConfirmResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm([FromRoute] long id, [FromBody] ConfirmRequest req, CancellationToken ct)
    {
        ConfirmResult data = await _service.ConfirmAsync(id, req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Ignore / 忽略</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "rowVersion":3, "reason":"测试样本" }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":200, "status":"Ignored" }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 并发冲突 / 该记录不在待确认状态
    /// </remarks>
    /// <response code="200">转 Ignored</response>
    [HttpPost("{id:long}/ignore")]
    [ProducesResponseType<ApiResponse<IgnoreResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Ignore([FromRoute] long id, [FromBody] IgnoreRequest req, CancellationToken ct)
    {
        IgnoreResult data = await _service.IgnoreAsync(id, req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>BatchConfirm / 批量确认</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "items":[ { "id":200, "tmdbId":27205, "mediaType":"movie", "categoryId":1, "rowVersion":3 } ] }
    /// ```
    /// 成功响应（部分失败也返 200 + code=0）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "succeeded":[200], "failed":[ { "id":201, "message":"..." } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — items 不能为空 / items 数量超过上限 50
    /// </remarks>
    /// <response code="200">逐条结果分流到 succeeded/failed</response>
    [HttpPost("batch-confirm")]
    [ProducesResponseType<ApiResponse<BatchConfirmResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BatchConfirm([FromBody] BatchConfirmRequest req, CancellationToken ct)
    {
        BatchConfirmResult data = await _service.BatchConfirmAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>BatchIgnore / 批量忽略</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "items":[ { "id":200, "rowVersion":3 } ], "reason":"重复样本" }
    /// ```
    /// 成功响应（部分失败也返 200 + code=0）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "succeeded":[200], "failed":[ { "id":201, "message":"..." } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — items 不能为空 / items 数量超过上限 50
    /// </remarks>
    /// <response code="200">逐条结果分流到 succeeded/failed</response>
    [HttpPost("batch-ignore")]
    [ProducesResponseType<ApiResponse<BatchIgnoreResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BatchIgnore([FromBody] BatchIgnoreRequest req, CancellationToken ct)
    {
        BatchIgnoreResult data = await _service.BatchIgnoreAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>TmdbSearch / 重搜 TMDB</summary>
    /// <remarks>
    /// 查询参数：query（必填）/ type（movie/tv/both，默认 both）/ year（可选）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "tmdbId":27205, "mediaType":"movie", "title":"盗梦空间", "originalTitle":"Inception", "year":2010, "posterUrl":"...", "originCountry":["US"], "popularity":84.5 } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 搜索词不能为空 / 记录不存在 / TMDB 服务异常（限流 / 网络抖动）
    /// </remarks>
    /// <response code="200">候选列表</response>
    [HttpGet("{id:long}/tmdb-search")]
    [ProducesResponseType<ApiResponse<TmdbSearchListResult>>(StatusCodes.Status200OK)]
    // 形参名不能叫 query：会与查询键 ?query= 同名，复杂类型绑定会把 query 当作前缀（查 query.Query）
    // 而漏绑必填 Query，[ApiController] 在进入 action 前即返 400。用 criteria 规避同名前缀冲突。
    public async Task<IActionResult> TmdbSearch([FromRoute] long id, [FromQuery] TmdbSearchListQuery criteria, CancellationToken ct)
    {
        TmdbSearchListResult data = await _service.TmdbSearchAsync(id, criteria, ct);
        return Ok(Wrap(data));
    }

    /// <summary>BindTmdb / 手动绑定</summary>
    /// <remarks>
    /// 请求体：
    /// ```json
    /// { "tmdbId":27205, "mediaType":"movie", "rowVersion":3 }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "id":200, "tmdbId":27205, "title":"盗梦空间", "year":2010, "rowVersion":4 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / 并发冲突 / TMDB ID 无效 / mediaType 非法 / 该记录不在待确认状态
    /// </remarks>
    /// <response code="200">绑定成功（不触发归档，仍需 confirm）</response>
    [HttpPost("{id:long}/bind-tmdb")]
    [ProducesResponseType<ApiResponse<BindTmdbResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BindTmdb([FromRoute] long id, [FromBody] BindTmdbRequest req, CancellationToken ct)
    {
        BindTmdbResult data = await _service.BindTmdbAsync(id, req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>TmdbDetail / 按 ID 取详情</summary>
    /// <remarks>
    /// 查询参数：tmdbId（必填）/ mediaType（movie/tv，必填）。
    /// 人工填 TMDB ID 后的预览用：拉标题/年份/海报/季数，确认无误后再走 confirm / bind-tmdb。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "tmdbId":27205, "mediaType":"movie", "title":"盗梦空间", "originalTitle":"Inception", "year":2010, "posterUrl":"...", "totalSeasons":null, "originCountry":["US"] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 记录不存在 / mediaType 非法 / TMDB ID 无效或无法查询
    /// </remarks>
    /// <response code="200">TMDB 详情</response>
    [HttpGet("{id:long}/tmdb-detail")]
    [ProducesResponseType<ApiResponse<TmdbDetailItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TmdbDetail([FromRoute] long id, [FromQuery] TmdbDetailQuery query, CancellationToken ct)
    {
        TmdbDetailItem data = await _service.TmdbDetailAsync(id, query, ct);
        return Ok(Wrap(data));
    }

    /// <summary>PreviewPaths / 去向预览</summary>
    /// <remarks>
    /// 只读：按各项 tmdbId/标题/年份/季集 + 分类 TargetRoot 算确认后的 Plex 落点（与确认同命名规范）。属「看队列」范畴，匿名可见。
    /// 请求体：
    /// ```json
    /// { "items":[ { "key":"200", "tmdbId":27205, "mediaType":"movie", "title":"盗梦空间", "year":2010, "season":null, "episode":null, "episodeEnd":null, "categoryId":1, "fileName":"Inception.2010.mkv" } ] }
    /// ```
    /// 成功响应（单项字段不全则该项 error 非空、relativePath/fullPath 为 null）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "entries":[ { "key":"200", "relativePath":"盗梦空间 (2010) {tmdb-27205}/盗梦空间 (2010) {tmdb-27205}.mkv", "fullPath":"D:/Movies/...", "error":null } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — items 不能为空 / items 数量超过上限 500
    /// </remarks>
    /// <response code="200">逐项去向（error 与路径互斥）</response>
    [HttpPost("preview-paths")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<ReviewPreviewPathResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewPaths([FromBody] ReviewPreviewPathRequest req, CancellationToken ct)
    {
        ReviewPreviewPathResult data = await _service.PreviewPathsAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>CheckFiles / 文件检查</summary>
    /// <remarks>
    /// 校验各项源文件是否仍存在；不存在的转 Ignored 移出待确认队列（保留记录 + 原因，便于审计），存在的保留。属写操作，要 Admin。
    /// 请求体：
    /// ```json
    /// { "items":[ { "id":200, "rowVersion":3 } ] }
    /// ```
    /// 成功响应（部分失败也返 200 + code=0）：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "removed":[200], "kept":3, "failed":[ { "id":201, "message":"记录已被其他用户修改，请刷新" } ] }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — items 不能为空 / items 数量超过上限 500
    /// </remarks>
    /// <response code="200">removed=已移出队列 / kept=仍存在 / failed=并发或状态冲突</response>
    [HttpPost("check-files")]
    [ProducesResponseType<ApiResponse<CheckFilesResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckFiles([FromBody] CheckFilesRequest req, CancellationToken ct)
    {
        CheckFilesResult data = await _service.CheckFilesAsync(req, ct);
        return Ok(Wrap(data));
    }
}
