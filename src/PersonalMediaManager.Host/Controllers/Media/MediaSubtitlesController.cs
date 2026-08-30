using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Subtitles;
using PersonalMediaManager.Application.Services.Subtitles;

namespace PersonalMediaManager.Host.Controllers.Media;

/// <summary>Media subtitles / 字幕下载</summary>
/// <remarks>
/// 已归档完成（Completed）媒体记录的外部字幕源（Assrt）手动搜索 / 文件清单 / 下载落盘 / 记录回看。
/// 防 SSRF：清单不暴露源站直链 url，下载按 subtitleId + fileIndex 服务端重取；落地名完全自构（防路径穿越）。
/// 全部仅 Admin（全局 FallbackPolicy；搜索 / 下载消耗外部源配额并写归档目录）。
/// </remarks>
[ApiController]
[Route("media/{id:long}/subtitles")]
public sealed class MediaSubtitlesController : ApiControllerBase
{
    private readonly ISubtitleDownloadService _service;

    public MediaSubtitlesController(ISubtitleDownloadService service)
    {
        _service = service;
    }

    /// <summary>Search subtitles / 搜索字幕</summary>
    /// <remarks>
    /// 查询参数：keyword 可空 —— 空白时服务端缺省用该记录原始文件名（去扩展名）；data.keyword 回传实际生效关键词。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "keyword": "Some.Show.S01E01.1080p", "items": [ { "subtitleId": "654321", "name": "某剧 第一季 第1集", "videoName": "Some.Show.S01E01", "languageHint": "简体", "format": "srt", "uploadTime": "2026-01-01 12:00:00", "voteScore": 5.0 } ] }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 媒体记录不存在 / 记录未归档完成 / 归档文件不存在 / 尚未配置 Assrt Token / 字幕源调用失败
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "仅已归档完成的记录可下载字幕", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">候选字幕列表（无结果时 items 为空数组）</response>
    [HttpGet("search")]
    [ProducesResponseType<ApiResponse<SubtitleSearchResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromRoute] long id, [FromQuery] string? keyword, CancellationToken ct)
    {
        SubtitleSearchResponse data = await _service.SearchAsync(id, keyword, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Subtitle files / 文件清单</summary>
    /// <remarks>
    /// 查询参数：subtitleId 必填（搜索结果里的源站字幕 Id）。压缩包逐项展开；items[].index = -1 表示顶层单文件。
    /// 刻意不返回源站直链 url（防 SSRF），下载时按 subtitleId + fileIndex 由服务端重取。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "subtitleId": "654321", "items": [ { "index": 0, "fileName": "Some.Show.S01E01.chs.srt", "sizeBytes": 45678 } ] }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 媒体记录不存在 / 记录未归档完成 / 归档文件不存在 / 尚未配置 Assrt Token / 字幕源调用失败（字幕不存在等）
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "尚未配置 Assrt Token，请前往 设置→字幕 配置", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">该字幕的可下载文件清单（可能为空集 = 无可下载直链）</response>
    [HttpGet("files")]
    [ProducesResponseType<ApiResponse<SubtitleFilesResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Files([FromRoute] long id, [FromQuery][Required] string subtitleId, CancellationToken ct)
    {
        SubtitleFilesResponse data = await _service.GetFilesAsync(id, subtitleId, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Download subtitle / 下载字幕</summary>
    /// <remarks>
    /// 请求体（fileIndex = -1 表示顶层单文件；subtitleName 仅用于记录展示，可空）：
    /// ```json
    /// { "subtitleId": "654321", "fileIndex": 0, "subtitleName": "某剧 第一季 第1集" }
    /// ```
    /// 服务端按 subtitleId 重取直链下载，落盘到归档视频旁：{视频基名}.{语言码}{扩展名}（同名按 .2 ~ .9 序号避让），
    /// 语言码按文件名识别（zh / zh-TW / en / ja / und），识别不出回退源站语言描述；并写一条 Subtitle_Download 记录。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "savedFileName": "某剧 (2023) - S01E01.zh.srt", "savedPath": "D:/Media/剧集/某剧 (2023)/Season 01/某剧 (2023) - S01E01.zh.srt", "language": "zh" }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 媒体记录不存在 / 记录未归档完成 / 归档文件不存在 / 尚未配置 Assrt Token / 所选字幕文件不存在 / 不支持的字幕格式 / 同语言字幕过多 / 字幕源下载失败
    /// - 9000 ServerError — 服务器内部错误（写盘失败等）
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "不支持的字幕格式：.zip", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">已落盘，返回落地文件名 / 完整路径 / 识别语言</response>
    [HttpPost("download")]
    [ProducesResponseType<ApiResponse<SubtitleDownloadResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download([FromRoute] long id, [FromBody] DownloadSubtitleRequest req, CancellationToken ct)
    {
        SubtitleDownloadResponse data = await _service.DownloadAsync(id, req, ct);
        return Ok(Wrap(data));
    }

    /// <summary>Subtitle downloads / 下载记录</summary>
    /// <remarks>
    /// 该媒体记录的字幕下载历史（按下载时间倒序）；仅要求记录存在，不要求当前仍是已归档状态。
    /// 成功响应：
    /// ```json
    /// { "code": 0, "message": "ok", "data": { "items": [ { "id": 1, "provider": "Assrt", "subtitleName": "某剧 第一季 第1集", "fileName": "某剧 (2023) - S01E01.zh.srt", "language": "zh", "fileSize": 45678, "createdAt": "2026-06-12T08:00:00+00:00" } ] }, "requestId": "..." }
    /// ```
    /// 错误码：
    /// - 1000 BusinessError — 媒体记录不存在
    /// - 9000 ServerError — 服务器内部错误
    /// 通用错误响应：
    /// ```json
    /// { "code": 1000, "message": "媒体记录不存在", "data": null, "requestId": "..." }
    /// ```
    /// </remarks>
    /// <response code="200">下载记录列表（按 CreatedAt 倒序）</response>
    [HttpGet("downloads")]
    [ProducesResponseType<ApiResponse<SubtitleDownloadListResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Downloads([FromRoute] long id, CancellationToken ct)
    {
        SubtitleDownloadListResponse data = await _service.ListDownloadsAsync(id, ct);
        return Ok(Wrap(data));
    }
}
