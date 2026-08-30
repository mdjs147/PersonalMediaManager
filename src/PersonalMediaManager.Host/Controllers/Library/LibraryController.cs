using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Library;
using PersonalMediaManager.Application.Services.Library;

namespace PersonalMediaManager.Host.Controllers.Library;

/// <summary>Library / 媒体库</summary>
/// <remarks>
/// 已归档作品海报墙（按 TMDB 作品聚合）+ 富化关联（演职员/类型/公司/电视台/关键词/分季分集）+ 库内搜索。
/// 浏览类只读端点匿名（§3.12）；刷新元数据 / 存在性扫描为写操作，需登录。
/// </remarks>
[ApiController]
[Route("library")]
public sealed class LibraryController : ApiControllerBase
{
    private readonly ILibraryService _service;
    private readonly IWorkEnrichmentService _enrichment;
    private readonly ITmdbImageProxy _imageProxy;

    public LibraryController(ILibraryService service, IWorkEnrichmentService enrichment, ITmdbImageProxy imageProxy)
    {
        _service = service;
        _enrichment = enrichment;
        _imageProxy = imageProxy;
    }

    /// <summary>List / 媒体库列表</summary>
    /// <remarks>
    /// 查询：type / categoryId / keyword / genreId / personId / companyId / networkId / keywordId / country / language / yearFrom / yearTo / sort（recent|year|rating|title）/ page / pageSize（≤100）。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "items":[ { "tmdbId":27205, "mediaType":"movie", "title":"盗梦空间", "year":2010, "rating":8.4, "categoryName":"电影", "fileCount":1, "missingFileCount":0, "latestArchivedAt":"...", "hasPoster":true, "genres":["科幻","动作"] } ], "total":128, "page":1, "pageSize":24 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 分页参数非法
    /// </remarks>
    /// <response code="200">作品分页（默认最近归档倒序）</response>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<LibraryListPage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] LibraryListQuery query, CancellationToken ct)
    {
        LibraryListPage data = await _service.ListAsync(query, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Facets / 筛选项</summary>
    /// <remarks>
    /// 返回库内已富化作品出现过的 类型/演职员/公司/电视台/关键词/国家/语言（各含 id、name、count），供前端筛选下拉。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "genres":[{ "id":"18","name":"剧情","count":42 }], "persons":[], "companies":[], "networks":[], "keywords":[], "countries":[{ "id":"CN","name":"CN","count":30 }], "languages":[] }, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">库内可用筛选项</response>
    [HttpGet("facets")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<LibraryFacets>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Facets(CancellationToken ct)
    {
        LibraryFacets data = await _service.GetFacetsAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>WorkDetail / 作品详情</summary>
    /// <remarks>
    /// Path：{tmdbId}；Query：mediaType（movie/tv，缺省 tv）。打开时惰性富化（TMDB 失败降级返回已有数据）。
    /// 返回富化元数据 + 演职员/公司/类型/关键词/季摘要 + 计数汇总 + 全部文件记录（含实时存在性 fileExists）。
    /// 文件写操作（重试/重新处理/删除/整剧）复用 /history/* 端点。
    /// 错误码：
    /// - 1000 BusinessError — 无效的 TmdbId / 作品不存在或无记录
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"作品不存在或无记录", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">作品级富化元数据 + 关联 + 文件记录</response>
    [HttpGet("{tmdbId:int}")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<LibraryWorkDetailResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> WorkDetail([FromRoute] int tmdbId, [FromQuery] string? mediaType, CancellationToken ct)
    {
        LibraryWorkDetailResponse data = await _service.GetWorkDetailAsync(tmdbId, mediaType ?? "tv", ct);
        return Ok(Wrap(data));
    }

    /// <summary>Season / 分季分集</summary>
    /// <remarks>
    /// Path：{tmdbId} / {seasonNumber}；Query：mediaType（缺省 tv）。首次展开惰性拉取 /tv/{id}/season/{n}。
    /// 返回该季每集（集名/简介/剧照/时长/评分）+ 本地文件映射（hasLocalFile / localStatus / localFileExists）。
    /// </remarks>
    /// <response code="200">某季分集 + 本地映射</response>
    [HttpGet("{tmdbId:int}/seasons/{seasonNumber:int}")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<LibrarySeasonResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Season([FromRoute] int tmdbId, [FromRoute] int seasonNumber, [FromQuery] string? mediaType, CancellationToken ct)
    {
        LibrarySeasonResponse data = await _service.GetSeasonAsync(tmdbId, seasonNumber, mediaType ?? "tv", ct);
        return Ok(Wrap(data));
    }

    /// <summary>Related / 相关作品</summary>
    /// <remarks>
    /// Path：{tmdbId}；Query：mediaType（缺省 tv）。按共享 类型/演职员/公司 计分，仅返回库内已归档作品。
    /// </remarks>
    /// <response code="200">相关作品列表（计分倒序）</response>
    [HttpGet("{tmdbId:int}/related")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<IReadOnlyList<LibraryRelatedItem>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Related([FromRoute] int tmdbId, [FromQuery] string? mediaType, CancellationToken ct)
    {
        IReadOnlyList<LibraryRelatedItem> data = await _service.GetRelatedAsync(tmdbId, mediaType ?? "tv", ct);
        return Ok(Wrap(data));
    }

    /// <summary>Refresh / 刷新单部</summary>
    /// <remarks>
    /// Path：{tmdbId}；Query：mediaType（缺省 tv）。强制重新拉 TMDB 富化数据覆盖 Media_Work 及关联、清空分集缓存。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":true, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未配置 TMDB ApiKey / TMDB 调用失败
    /// </remarks>
    /// <response code="200">是否成功富化</response>
    [HttpPost("{tmdbId:int}/refresh-metadata")]
    [Authorize]
    [ProducesResponseType<ApiResponse<bool>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshMetadata([FromRoute] int tmdbId, [FromQuery] string? mediaType, CancellationToken ct)
    {
        bool ok = await _enrichment.EnrichAsync(tmdbId, mediaType ?? "tv", force: true, ct);
        return Ok(Wrap(ok));
    }

    /// <summary>RefreshAll / 整库刷新</summary>
    /// <remarks>
    /// 对全部已归档作品批量富化（命中 TTL 未过期的跳过），供库内搜索覆盖历史作品；返回处理作品数。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":42, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">已处理作品数</response>
    [HttpPost("refresh-metadata-all")]
    [Authorize]
    [ProducesResponseType<ApiResponse<int>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshAll(CancellationToken ct)
    {
        int n = await _enrichment.EnrichAllAsync(ct);
        return Ok(Wrap(n));
    }

    /// <summary>ScanExistence / 存在性扫描</summary>
    /// <remarks>
    /// 对全部已完成记录 File.Exists(TargetPath) 落 FileMissing / FileCheckedAt，供列表/详情打缺失标记。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "checked":216, "missing":3 }, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">扫描汇总</response>
    [HttpPost("scan-existence")]
    [Authorize]
    [ProducesResponseType<ApiResponse<ScanExistenceResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ScanExistence(CancellationToken ct)
    {
        ScanExistenceResult data = await _service.ScanExistenceAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>RescanAudio / 存量音频重扫</summary>
    /// <remarks>
    /// 对未探测过的已完成记录批量 ffprobe 探测 TargetPath，回写不兼容音轨标记（仅打标不重混）；需先在「设置 → 常规 → 音频」开启总开关并配 ffmpeg 路径，否则返回 1000。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "checked":120, "probed":118, "incompatible":7, "skipped":2 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError：未启用音频检查 / codec 清单为空 / ffprobe 路径无效
    /// ```json
    /// { "code":1000, "message":"音频不兼容轨检查未启用…", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">重扫汇总</response>
    /// <response code="400">配置缺失（未启用 / 无 ffprobe）</response>
    [HttpPost("rescan-audio")]
    [Authorize]
    [ProducesResponseType<ApiResponse<AudioRescanResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RescanAudio(CancellationToken ct)
    {
        AudioRescanResult data = await _service.RescanAudioAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>ScanOrphans / 孤儿扫描</summary>
    /// <remarks>
    /// 遍历各分类归档根，列出磁盘有、DB 无记录的媒体文件（删了记录但文件留在库里的找回入口）；只读，不动文件。
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":3, "items":[ {"path":"D:/Media/电影/X (2020) {tmdb-1}/X (2020) {tmdb-1}.mkv","fileName":"X (2020) {tmdb-1}.mkv","size":1572864000,"categoryId":1,"categoryName":"电影","tmdbId":1} ] }, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">孤儿文件清单</response>
    [HttpPost("orphans/scan")]
    [Authorize]
    [ProducesResponseType<ApiResponse<OrphanScanResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ScanOrphans(CancellationToken ct)
    {
        OrphanScanResult data = await _service.ScanOrphansAsync(ct);
        return Ok(Wrap(data));
    }

    /// <summary>ClaimOrphans / 孤儿认领</summary>
    /// <remarks>
    /// 把选中孤儿文件重新投入处理管线重新登记入库；已在规范位的由归档层识别「已就位」直接登记、不搬动。
    /// 请求体：
    /// ```json
    /// { "paths":["D:/Media/电影/X (2020) {tmdb-1}/X (2020) {tmdb-1}.mkv"] }
    /// ```
    /// 成功响应：
    /// ```json
    /// { "code":0, "message":"ok", "data":{ "total":1, "admitted":1, "skipped":0 }, "requestId":"..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 未指定要认领的孤儿文件
    /// 通用错误响应：
    /// ```json
    /// { "code":1000, "message":"未指定要认领的孤儿文件", "data":null, "requestId":"..." }
    /// ```
    /// </remarks>
    /// <response code="200">认领入队结果</response>
    [HttpPost("orphans/claim")]
    [Authorize]
    [ProducesResponseType<ApiResponse<ClaimOrphansResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClaimOrphans([FromBody] ClaimOrphansRequest req, CancellationToken ct)
    {
        ClaimOrphansResult data = await _service.ClaimOrphansAsync(req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Poster / 作品海报</summary>
    /// <remarks>
    /// 流式返回该 TMDB 作品本地缓存海报（AppPaths.PostersDir/{tmdbId}.jpg）；未缓存 → 404，前端降级占位。
    /// </remarks>
    /// <response code="200">海报字节流（image/jpeg）</response>
    /// <response code="404">无缓存海报</response>
    [HttpGet("poster/{tmdbId:int}")]
    [AllowAnonymous]
    [Produces("image/jpeg")]
    [ProducesResponseType(typeof(PhysicalFileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Poster([FromRoute] int tmdbId)
    {
        string? path = _service.GetPosterPath(tmdbId);
        if (path is null) return NotFound();
        return PhysicalFile(path, "image/jpeg");
    }

    /// <summary>Image / 图片代理</summary>
    /// <remarks>
    /// 按 TMDB 相对路径取背景图/人物照/剧照/Logo，后端按需缓存后流式返回（隐私：浏览器不直连 TMDB）。
    /// Path：{size}（w92/w300/w500/original 等白名单）/ {path}（TMDB 图片名，无需前导斜杠）。未取到 → 404。
    /// </remarks>
    /// <response code="200">图片字节流</response>
    /// <response code="404">非法尺寸/路径或拉取失败</response>
    [HttpGet("tmdb-image/{size}/{*path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Image([FromRoute] string size, [FromRoute] string path, CancellationToken ct)
    {
        string? file = await _imageProxy.GetCachedImageAsync(size, path, ct);
        if (file is null) return NotFound();
        return PhysicalFile(file, ContentTypeOf(file));
    }

    private static string ContentTypeOf(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
    }
}
