using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Scan;

/// <summary>整理演练请求</summary>
/// <param name="Path">待演练的文件路径（可不真实存在，按文件名/路径段解析）</param>
public sealed record DryRunRequest(
    [RequiredNotBlank(ErrorMessage = "路径不能为空")]
    string Path);

/// <summary>整理演练结果</summary>
/// <remarks>
/// 只读预览：规则解析 + TMDB 匹配 + Plex 命名，全程不调用 AI、不移动文件、不写处理记录。
/// 与「手动整理」一致用 WatchFolderId=0 上下文，故预览结果即手动整理的实际走向。
/// </remarks>
/// <param name="Path">规范化后的输入路径</param>
/// <param name="FileName">文件名（含扩展名）</param>
/// <param name="Title">规则解析标题（never null，无命中时回退文件名 stem）</param>
/// <param name="Year">年份；无法识别为 null</param>
/// <param name="MediaType">movie / tv / unknown</param>
/// <param name="Season">季号（剧集）</param>
/// <param name="Episode">集号（剧集）</param>
/// <param name="EpisodeEnd">双集合并结束集；单集为 null</param>
/// <param name="Confidence">规则置信度 0~1</param>
/// <param name="HasSpecialChars">是否命中特殊字符规则（强制走 AI）</param>
/// <param name="MatchedRuleId">命中的解析规则主键；未命中为 null</param>
/// <param name="Outcome">演练判定结果</param>
/// <param name="OutcomeReason">判定原因（中文说明）</param>
/// <param name="TmdbQueried">是否实际查询了 TMDB（规则不足时不查）</param>
/// <param name="Candidates">TMDB 候选列表</param>
/// <param name="Picked">采纳的候选（相似度最高）；未采纳为 null</param>
/// <param name="PreviewRelativePath">采纳候选后的 Plex 相对路径（不含分类根目录）；无法预览为 null</param>
/// <param name="PreviewNote">命名预览补充说明</param>
public sealed record DryRunPreview(
    string Path,
    string FileName,
    string? Title,
    int? Year,
    string MediaType,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    double Confidence,
    bool HasSpecialChars,
    long? MatchedRuleId,
    DryRunOutcome Outcome,
    string OutcomeReason,
    bool TmdbQueried,
    IReadOnlyList<DryRunCandidate> Candidates,
    DryRunCandidate? Picked,
    string? PreviewRelativePath,
    string? PreviewNote);

/// <summary>演练判定结果</summary>
public enum DryRunOutcome
{
    /// <summary>规则 + TMDB 即可确定，已预览命名路径（实际处理直接归档，不触发 AI）</summary>
    WouldArchive = 1,

    /// <summary>规则不足 / TMDB 候选不符，实际处理会触发 AI 兜底（演练不调用 AI）</summary>
    WouldCallAi = 2,

    /// <summary>字段不全（剧集缺季集 / 年份缺失等），实际会转人工补全</summary>
    WouldReview = 3,
}

/// <summary>演练 TMDB 候选（精简投影）</summary>
public sealed record DryRunCandidate(
    int TmdbId,
    string MediaType,
    string? Title,
    string? OriginalTitle,
    int? Year,
    double Popularity);
