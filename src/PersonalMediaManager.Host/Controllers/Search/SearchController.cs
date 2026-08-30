using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Search;
using PersonalMediaManager.Application.Services.Search;

namespace PersonalMediaManager.Host.Controllers.Search;

/// <summary>Search / 全局搜索</summary>
/// <remarks>
/// 顶栏统一搜索：一次 GET 返回历史 / 媒体库 / 待确认三域各 topN 命中 + 各域真实总数。
/// 公开端点：只读，匿名可读（与历史 / 媒体库列表同口径）。
/// 关键词空 / 纯空白 → 200 + 三组空数组（不报错）。
/// 成功响应：
/// ```json
/// { "code":0, "message":"ok", "data":{ "query":"x", "history":[{ "id":1, "fileName":"a.mkv", "title":"标题", "year":2020, "status":"Completed", "tmdbId":123, "tmdbMediaType":"tv" }], "library":[{ "tmdbId":123, "mediaType":"tv", "title":"标题", "year":2020, "hasPoster":true, "fileCount":12 }], "review":[{ "id":2, "fileName":"b.mkv", "title":null, "year":null }], "counts":{ "history":1, "library":1, "review":1 } }, "requestId":"..." }
/// ```
/// 错误码：
/// - 9000 ServerError — 数据库查询失败
/// </remarks>
[ApiController]
[Route("search")]
[AllowAnonymous]
public sealed class SearchController : ApiControllerBase
{
    private readonly ISearchService _service;

    public SearchController(ISearchService service) { _service = service; }

    /// <summary>Search / 全局搜索</summary>
    /// <remarks>
    /// 查询参数：q（关键词，trim 截断 100）/ limitPerGroup（每域上限，钳 1..20，缺省 5）/ scopes（逗号分隔 history,library,review，缺省全查）。
    /// </remarks>
    /// <response code="200">三域命中 + 各域总数（关键词空时为空结果）</response>
    [HttpGet]
    [ProducesResponseType<ApiResponse<GlobalSearchResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] GlobalSearchQuery query, CancellationToken ct)
    {
        GlobalSearchResponse data = await _service.ListAsync(query, ct);
        return Ok(Wrap(data));
    }
}
