using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>测试集 AI 顾问（C1：Triage 判定；C2 追加 SuggestRule 正则生成）</summary>
/// <remarks>
/// 基于通用 <see cref="PersonalMediaManager.Application.Contracts.IAiChatClient"/> 构建高层能力：拼专用 prompt + 解析专用 JSON。
/// Triage：给定一个解析失败 / 待判定的样本 + 现有测试集样本节选，判断是否值得纳入回归集，并给出对该样本的解析建议。
/// 实现位于 Application/Services/Parse/AiAdvisor.cs（纯编排 + prompt，不引 EF/HTTP）。
/// </remarks>
public interface IAiAdvisor
{
    /// <summary>判断样本是否值得纳入测试集，并给出 AI 的解析建议（供人工确认基线参考）</summary>
    Task<TriageAdvice> TriageAsync(TriageInput input, CancellationToken ct = default);

    /// <summary>为样本生成一条 .NET 正则解析规则（命名捕获 title/year/season/episode），供人工审核后落 Parse_Rule</summary>
    Task<RuleSuggestion> SuggestRuleAsync(SuggestRuleInput input, CancellationToken ct = default);
}

/// <summary>Triage 输入</summary>
/// <param name="SamplePath">待判定样本完整路径</param>
/// <param name="WatchRootPath">样本所在监控根（可空；用于切出相对路径段）</param>
/// <param name="FailureReason">该样本历史失败原因（可空；来自 Media_Item.ErrorMessage / Notes）</param>
/// <param name="ExistingSamplePaths">现有测试集样本路径节选（供 AI 判重，最多传约 30 条）</param>
public sealed record TriageInput(
    string SamplePath,
    string? WatchRootPath,
    string? FailureReason,
    IReadOnlyList<string> ExistingSamplePaths);

/// <summary>Triage 输出（AI 判定 + 解析建议）</summary>
/// <param name="WorthAdding">是否建议纳入回归集</param>
/// <param name="Reason">中文理由（简短）</param>
/// <param name="SimilarityToExisting">与现有样本的相似度 0~1（高=雷同，纳入价值低）</param>
/// <param name="SuggestedTitle">AI 对该样本的标题解析建议</param>
/// <param name="SuggestedYear">年份建议</param>
/// <param name="SuggestedMediaType">类型建议（Movie/Tv/null）</param>
/// <param name="SuggestedSeason">季建议</param>
/// <param name="SuggestedEpisode">集建议</param>
public sealed record TriageAdvice(
    bool WorthAdding,
    string Reason,
    double SimilarityToExisting,
    string? SuggestedTitle,
    int? SuggestedYear,
    MediaType? SuggestedMediaType,
    int? SuggestedSeason,
    int? SuggestedEpisode);

/// <summary>SuggestRule 输入（样本 + 期望解析结果，AI 据此反推正则）</summary>
/// <param name="SamplePath">样本完整路径</param>
/// <param name="WatchRootPath">监控根（可空）</param>
/// <param name="ExpectedTitle">期望标题（可空；有则 AI 以此为目标输出）</param>
/// <param name="ExpectedYear">期望年份</param>
/// <param name="ExpectedMediaType">期望类型（Movie/Tv/null）</param>
/// <param name="ExpectedSeason">期望季</param>
/// <param name="ExpectedEpisode">期望集</param>
public sealed record SuggestRuleInput(
    string SamplePath,
    string? WatchRootPath,
    string? ExpectedTitle,
    int? ExpectedYear,
    MediaType? ExpectedMediaType,
    int? ExpectedSeason,
    int? ExpectedEpisode);

/// <summary>SuggestRule 输出（一条解析规则的建议）</summary>
/// <param name="Pattern">.NET 正则（命名捕获 title/year/season/episode/episodeEnd）</param>
/// <param name="Scope">建议作用域（决定正则匹配输入范围）</param>
/// <param name="DefaultType">建议默认类型（movie/tv/null）</param>
/// <param name="Explanation">中文说明（正则如何匹配，供人工审核）</param>
public sealed record RuleSuggestion(
    string Pattern,
    ParseScope Scope,
    string? DefaultType,
    string Explanation);
