using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Subtitles;

/// <summary>字幕源配置响应 DTO（不回显 Token 密文，仅暴露 HasToken）</summary>
public sealed record SubtitleSettingResponse(
    bool HasToken,
    string BaseUrl,
    int TimeoutSeconds,
    bool UseProxy);

/// <summary>更新请求（Token 三态：null=不变 / ""=清空 / 非空=替换）</summary>
public sealed record UpdateSubtitleSettingRequest(
    string? Token,
    [RequiredNotBlank(ErrorMessage = "BaseUrl 不能为空")]
    [HttpUrl(ErrorMessage = "BaseUrl 必须是 http/https 绝对地址")]
    [MaxLength(200, ErrorMessage = "BaseUrl 长度不能超过 200 字符")]
    string BaseUrl,
    [Range(5, 120, ErrorMessage = "超时秒数必须在 [5, 120] 范围内")]
    int TimeoutSeconds,
    bool UseProxy);

/// <summary>连通性 + 配额测试响应</summary>
public sealed record TestSubtitleResponse(
    bool Success,
    int? Quota,
    long ElapsedMilliseconds,
    string? ErrorMessage);
