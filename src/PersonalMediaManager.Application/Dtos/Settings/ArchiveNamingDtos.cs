using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Settings;

/// <summary>归档命名模板配置（读）：3 个文件名主体模板 + 季标题后缀开关</summary>
public sealed record ArchiveNamingResponse(
    string MovieNameTemplate,
    string TvShowFolderTemplate,
    string TvEpisodeNameTemplate,
    bool SeasonFolderIncludeTitle);

/// <summary>归档命名模板更新请求；非空校验在绑定阶段，模板合法性（占位符 / {tmdb / 路径符）在服务层经 NamingTemplateRenderer 校验</summary>
/// <remarks>
/// 三模板必填非空（[RequiredNotBlank]）；更深的模板语义校验（拒绝 {tmdb 字面量 / 路径分隔符 / 未知占位符、
/// 单集模板必须含 {episode}）由服务层调 NamingTemplateRenderer.ValidateTemplate 完成，失败抛 BusinessException（1000）。
/// 季标题开关为布尔，无需额外校验。
/// </remarks>
public sealed record UpdateArchiveNamingRequest(
    [RequiredNotBlank(ErrorMessage = "电影命名模板不能为空")] string MovieNameTemplate,
    [RequiredNotBlank(ErrorMessage = "剧集根目录模板不能为空")] string TvShowFolderTemplate,
    [RequiredNotBlank(ErrorMessage = "单集命名模板不能为空")] string TvEpisodeNameTemplate,
    bool SeasonFolderIncludeTitle);

/// <summary>归档命名模板预览请求：草稿模板 → 后端用固定示例渲染完整相对路径 + 返回校验错误</summary>
/// <remarks>
/// 三模板均必填非空；与实际归档共用 NamingTemplateRenderer + PlexNamingConventions 渲染管道，保证预览即真实落点形态。
/// 后端用内置固定示例（电影：盗梦空间 2010 tmdb27205 导演剪辑版；剧集单集：鬼灭之刃 2019 tmdb85937 S03E01 锻刀村篇；双集 S01E08-E09）。
/// </remarks>
public sealed record ArchiveNamingPreviewRequest(
    [RequiredNotBlank(ErrorMessage = "电影命名模板不能为空")] string MovieNameTemplate,
    [RequiredNotBlank(ErrorMessage = "剧集根目录模板不能为空")] string TvShowFolderTemplate,
    [RequiredNotBlank(ErrorMessage = "单集命名模板不能为空")] string TvEpisodeNameTemplate,
    bool SeasonFolderIncludeTitle);

/// <summary>单条预览样例渲染结果：示例标签 + 渲染出的完整相对路径（含 {tmdb-NNN} 后缀展示）；失败时 error 非空</summary>
public sealed record ArchiveNamingPreviewSample(string Label, string? RelativePath, string? Error);

/// <summary>归档命名模板预览结果：每模板的校验错误清单 + 各样例渲染路径</summary>
/// <remarks>
/// MovieErrors / TvShowFolderErrors / TvEpisodeErrors 为各模板 NamingTemplateRenderer.ValidateTemplate 的错误清单（空 = 合法）；
/// Samples 为用合法模板渲染的样例路径（任一模板非法则其相关样例 error 携带原因，整体不抛异常便于前端实时展示）。
/// </remarks>
public sealed record ArchiveNamingPreviewResponse(
    IReadOnlyList<string> MovieErrors,
    IReadOnlyList<string> TvShowFolderErrors,
    IReadOnlyList<string> TvEpisodeErrors,
    IReadOnlyList<ArchiveNamingPreviewSample> Samples);
