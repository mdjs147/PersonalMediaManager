using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Statistics;
using PersonalMediaManager.Application.Services.Statistics;

namespace PersonalMediaManager.Host.Controllers.Statistics;

/// <summary>Statistics / 统计分析</summary>
/// <remarks>
/// 媒体库内容洞察：库构成（电影/剧集、年代、评分、类型、国家、分类）+ 近 12 月入库趋势 + 存储 Top。
/// 只读匿名（与 Dashboard 一致，需求文档 §3.12 概览类页面对所有人可见）。
/// </remarks>
[ApiController]
[Route("statistics")]
[AllowAnonymous]
public sealed class StatisticsController : ApiControllerBase
{
    private readonly IStatisticsService _service;

    public StatisticsController(IStatisticsService service)
    {
        _service = service;
    }

    /// <summary>Overview / 统计总览</summary>
    /// <remarks>
    /// 查询参数：range（30d / 90d / 1y / all，缺省 all）—— 按归档时间窗过滤整页；30d/90d 趋势按天、1y/all 按月。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "range":"all", "trendGranularity":"month", "summary":{ "movieCount":12, "tvCount":5, "workCount":17, "fileCount":140, "totalSize":880234901, "avgRating":7.8, "ratedCount":15, "oldestYear":1994, "newestYear":2026 }, "trend":[{ "bucket":"2026-06", "count":18 }], "decadeDistribution":[{ "decade":2020, "count":9 }], "ratingHistogram":[{ "label":"8~9", "count":6 }], "genreTop":[{ "name":"动画", "count":7 }], "countryTop":[{ "name":"JP", "count":8 }], "categoryDistribution":[{ "name":"电影", "count":12 }], "storageTop":[{ "tmdbId":603, "mediaType":"movie", "title":"黑客帝国", "year":1999, "size":12340982341 }], "identification":{ "total":140, "ruleCount":98, "aiCount":22, "hybridCount":6, "manualCount":4, "otherCount":10, "aiInvolvedCount":31, "reviewInvolvedCount":12 } }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 9000 ServerError — DB 查询失败
    ///
    /// 错误响应：
    /// ```json
    /// { "code":9000, "message":"服务器错误", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">统计分析总览聚合</response>
    [HttpGet("overview")]
    [ProducesResponseType<ApiResponse<StatisticsOverview>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview([FromQuery] string? range, CancellationToken ct)
    {
        StatisticsOverview data = await _service.GetOverviewAsync(range, ct);
        return Ok(Wrap(data));
    }
}
