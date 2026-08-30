using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Tmdb;

/// <summary>TMDB 配置响应 DTO（不暴露 ApiKey 密文）</summary>
public sealed record TmdbSettingResponse(
    bool HasApiKey,
    string Language,
    string FallbackLanguage,
    int CandidateThreshold,
    int RateLimitPerSecond,
    int MetadataCacheHours,
    int SearchCacheMinutes,
    double ScoreWeightTitle,
    double ScoreWeightYear,
    double ScoreWeightPopularity,
    double ScoreWeightLanguage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>更新请求（ApiKey 三态：null=不变 / ""=清空 / 非空=替换）</summary>
public sealed record UpdateTmdbSettingRequest(
    string? ApiKey,
    [RequiredNotBlank(ErrorMessage = "语言不能为空")]
    [MaxLength(16, ErrorMessage = "语言长度不能超过 16 字符")]
    string Language,
    [RequiredNotBlank(ErrorMessage = "兜底语言不能为空")]
    [MaxLength(16, ErrorMessage = "兜底语言长度不能超过 16 字符")]
    string FallbackLanguage,
    [Range(1, 50, ErrorMessage = "候选阈值必须在 [1, 50] 范围内")]
    int CandidateThreshold,
    [Range(1, 200, ErrorMessage = "每秒速率限制必须在 [1, 200] 范围内")]
    int RateLimitPerSecond,
    [Range(1, 720, ErrorMessage = "元数据缓存小时数必须在 [1, 720] 范围内")]
    int MetadataCacheHours,
    [Range(1, 10080, ErrorMessage = "搜索缓存分钟数必须在 [1, 10080] 范围内")]
    int SearchCacheMinutes,
    [Range(0.0, 1.0, ErrorMessage = "标题权重必须在 [0, 1] 范围内")]
    double ScoreWeightTitle,
    [Range(0.0, 1.0, ErrorMessage = "年份权重必须在 [0, 1] 范围内")]
    double ScoreWeightYear,
    [Range(0.0, 1.0, ErrorMessage = "热度权重必须在 [0, 1] 范围内")]
    double ScoreWeightPopularity,
    [Range(0.0, 1.0, ErrorMessage = "语言权重必须在 [0, 1] 范围内")]
    double ScoreWeightLanguage);

public sealed record TestTmdbResponse(
    bool Success,
    int? HttpStatus,
    double ElapsedMilliseconds,
    string? ErrorMessage);

public sealed record ClearTmdbCacheResponse(int SearchRowsDeleted, int MetadataRowsDeleted);
