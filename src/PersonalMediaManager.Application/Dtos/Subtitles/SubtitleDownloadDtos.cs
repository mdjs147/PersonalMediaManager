using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Subtitles;

/// <summary>字幕搜索响应（实际生效关键词 + 候选列表）</summary>
public sealed record SubtitleSearchResponse(
    string Keyword,
    IReadOnlyList<SubtitleSearchItem> Items);

/// <summary>搜索候选单条</summary>
/// <param name="LanguageHint">源站语言描述（如「简体」「双语」），仅供展示参考</param>
public sealed record SubtitleSearchItem(
    string SubtitleId,
    string Name,
    string? VideoName,
    string? LanguageHint,
    string? Format,
    string? UploadTime,
    double? VoteScore);

/// <summary>字幕文件清单响应（刻意不暴露源站直链 url，防 SSRF；下载时后端按 SubtitleId + Index 重取）</summary>
public sealed record SubtitleFilesResponse(
    string SubtitleId,
    IReadOnlyList<SubtitleFileItem> Items);

/// <summary>文件清单单条（Index=-1 表示顶层单文件，非压缩包展开）</summary>
public sealed record SubtitleFileItem(
    int Index,
    string FileName,
    long? SizeBytes);

/// <summary>下载请求（FileIndex=-1 表示顶层单文件）</summary>
public sealed record DownloadSubtitleRequest(
    [RequiredNotBlank(ErrorMessage = "字幕 Id 不能为空")]
    [MaxLength(64, ErrorMessage = "字幕 Id 过长")]
    string SubtitleId,
    [Range(-1, int.MaxValue, ErrorMessage = "文件序号不能小于 -1")]
    int FileIndex,
    [MaxLength(500, ErrorMessage = "字幕名称过长")]
    string? SubtitleName);

/// <summary>下载结果（落盘文件名 + 路径 + 识别出的语言码）</summary>
public sealed record SubtitleDownloadResponse(
    string SavedFileName,
    string SavedPath,
    string Language);

/// <summary>下载记录列表响应</summary>
public sealed record SubtitleDownloadListResponse(IReadOnlyList<SubtitleDownloadItem> Items);

/// <summary>下载记录单条</summary>
public sealed record SubtitleDownloadItem(
    long Id,
    string Provider,
    string? SubtitleName,
    string FileName,
    string Language,
    long FileSize,
    DateTimeOffset CreatedAt);
