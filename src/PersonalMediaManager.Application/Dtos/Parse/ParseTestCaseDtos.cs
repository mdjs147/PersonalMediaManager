using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Parse;

/// <summary>Parse_TestCase 响应 DTO（含所有字段 + RowVersion 乐观并发）</summary>
/// <remarks>
/// LastRunResult / AiVerdict 两个 JSON 列暂以原始字符串返前端（PR3 接回归引擎后由前端按需解析展示）。
/// </remarks>
public sealed record ParseTestCaseResponse(
    long Id,
    ParseTestCaseSource Source,
    ParseTestCaseStatus Status,
    string SamplePath,
    string? WatchRootPath,
    string? ExpectedTitle,
    int? ExpectedYear,
    MediaType? ExpectedMediaType,
    int? ExpectedSeason,
    int? ExpectedEpisode,
    DateTimeOffset? LastRunAt,
    ParseTestCaseRunStatus LastRunStatus,
    string? LastRunResult,
    long? LastMatchedRuleId,
    string? AiVerdict,
    string? AiSuggestedRulePattern,
    string? Notes,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>手动新增样本（默认 Source=Manual, Status=Active；Expected* 可直接填）</summary>
/// <remarks>ExpectedMediaType≠Both 的取值约束仍由 ParseTestCaseService 校验（排除式枚举判定，非声明式取值集）。</remarks>
public sealed record CreateParseTestCaseRequest(
    [RequiredNotBlank(ErrorMessage = "样本路径不能为空")]
    [MaxLength(1000, ErrorMessage = "样本路径长度不能超过 1000 字符")]
    string SamplePath,
    [MaxLength(1000, ErrorMessage = "WatchRootPath 长度不能超过 1000 字符")]
    string? WatchRootPath = null,
    [MaxLength(300, ErrorMessage = "ExpectedTitle 长度不能超过 300 字符")]
    string? ExpectedTitle = null,
    // null 留空合法；[Range] 对 null 自动放行
    [Range(1800, 2200, ErrorMessage = "ExpectedYear 必须在 [1800, 2200] 范围内")]
    int? ExpectedYear = null,
    MediaType? ExpectedMediaType = null,
    [Range(0, int.MaxValue, ErrorMessage = "ExpectedSeason 必须 >= 0")]
    int? ExpectedSeason = null,
    [Range(0, int.MaxValue, ErrorMessage = "ExpectedEpisode 必须 >= 0")]
    int? ExpectedEpisode = null,
    [MaxLength(500, ErrorMessage = "Notes 长度不能超过 500 字符")]
    string? Notes = null);

/// <summary>更新样本基本字段（不允许改 Source / Status / LastRun* / Ai*；这些走专用接口或机器维护）</summary>
/// <remarks>ExpectedMediaType≠Both 的取值约束仍由 ParseTestCaseService 校验（排除式枚举判定，非声明式取值集）。</remarks>
public sealed record UpdateParseTestCaseRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "样本路径不能为空")]
    [MaxLength(1000, ErrorMessage = "样本路径长度不能超过 1000 字符")]
    string SamplePath,
    [MaxLength(1000, ErrorMessage = "WatchRootPath 长度不能超过 1000 字符")]
    string? WatchRootPath,
    [MaxLength(300, ErrorMessage = "ExpectedTitle 长度不能超过 300 字符")]
    string? ExpectedTitle,
    // null 留空合法；[Range] 对 null 自动放行
    [Range(1800, 2200, ErrorMessage = "ExpectedYear 必须在 [1800, 2200] 范围内")]
    int? ExpectedYear,
    MediaType? ExpectedMediaType,
    [Range(0, int.MaxValue, ErrorMessage = "ExpectedSeason 必须 >= 0")]
    int? ExpectedSeason,
    [Range(0, int.MaxValue, ErrorMessage = "ExpectedEpisode 必须 >= 0")]
    int? ExpectedEpisode,
    [MaxLength(500, ErrorMessage = "Notes 长度不能超过 500 字符")]
    string? Notes,
    long RowVersion);

public sealed record DeleteParseTestCaseRequest(long Id);

/// <summary>列表查询（按 Status 过滤 + 分页 + 关键词模糊匹配 SamplePath/ExpectedTitle）</summary>
/// <remarks>
/// Status=null 返回全部；Page 从 1 起；PageSize 上限 200。
/// Keyword 走 SQL LIKE %xxx%（SQLite 默认大小写不敏感对 ASCII；中文按 binary 比较）。
/// </remarks>
public sealed record ParseTestCaseListQuery(
    ParseTestCaseStatus? Status = null,
    string? Keyword = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>列表响应（分页）</summary>
public sealed record ParseTestCaseListResponse(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<ParseTestCaseResponse> Items);

/// <summary>从 Failed 历史灌入暂存区</summary>
/// <remarks>
/// Limit 上限 200，默认 50；SinceUtc 可选下限（只取 LastAttemptAt &gt;= 该时间）。
/// 同 SamplePath 已存在 Parse_TestCase（任何状态）→ skip。
/// Expected* 一律不从 Failed 的 ParsedInfo 拷贝（Failed 解析本身就有问题，拷过来反而误导期望基线）。
/// </remarks>
public sealed record ImportFromFailedRequest(
    int Limit = 50,
    DateTimeOffset? SinceUtc = null);

/// <summary>灌入失败明细</summary>
public sealed record ImportFromFailedFailure(long MediaItemId, string SourcePath, string Message);

/// <summary>灌入结果（部分失败仍返 200）</summary>
public sealed record ImportFromFailedResult(
    int Imported,
    int Skipped,
    IReadOnlyList<ImportFromFailedFailure> Failures);

/// <summary>状态切换请求（PromoteToActive / Disable / ResetToTriage 通用）</summary>
public sealed record TransitionParseTestCaseStatusRequest(long Id, long RowVersion);

/// <summary>单条运行回归请求</summary>
public sealed record RunParseTestCaseRequest(long Id);

/// <summary>批量运行回归请求（默认仅跑 Status=Active；limit 上限 500）</summary>
public sealed record RunParseTestCasesBatchRequest(
    ParseTestCaseStatus? Status = ParseTestCaseStatus.Active,
    int Limit = 200);

/// <summary>单条失败明细</summary>
public sealed record RunBatchFailure(long Id, string SamplePath, string Message);

/// <summary>批量回归结果</summary>
/// <remarks>
/// Ran：实际尝试运行的条数（不含运行抛异常的）；
/// Pass / Fail：参与 Expected 比对且有结论的两类；
/// NotComparable：跑了但未设任何 Expected 字段（无可比基线），不计入 Pass/Fail；
/// Failures：执行过程抛异常的样本（每条不打断整体跑批）。
/// </remarks>
public sealed record RunParseTestCasesBatchResult(
    int Ran,
    int Pass,
    int Fail,
    int NotComparable,
    IReadOnlyList<RunBatchFailure> Failures);

/// <summary>批准 LastRunResult 为期望基线（拷贝 title/year/mediaType/season/episode 到 Expected*）</summary>
public sealed record ApproveAsExpectedRequest(long Id, long RowVersion);

/// <summary>交给 AI 判定是否值得纳入测试集（写入 AiVerdict，不自动改 Status）</summary>
public sealed record TriageWithAiRequest(long Id);

/// <summary>请 AI 为样本生成解析规则（写入 AiSuggestedRulePattern）</summary>
public sealed record SuggestRuleRequest(long Id);

/// <summary>AI 规则建议响应（供前端展示 + 预填 Parse_Rule 新增表单）</summary>
/// <param name="TestCaseId">来源样本 Id</param>
/// <param name="Pattern">.NET 正则（命名捕获 title/year/season/episode）</param>
/// <param name="Scope">建议作用域</param>
/// <param name="DefaultType">建议默认类型（movie/tv/null）</param>
/// <param name="Explanation">中文说明</param>
public sealed record RuleSuggestionResponse(
    long TestCaseId,
    string Pattern,
    ParseScope Scope,
    string? DefaultType,
    string Explanation);
