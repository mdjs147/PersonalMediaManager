using PersonalMediaManager.Application.Dtos.Parse;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>解析测试用例服务契约（Parse_TestCase CRUD + 暂存区灌入 + 状态流转）</summary>
/// <remarks>
/// PR2 范围：CRUD + ImportFromFailed + Promote/Disable/ResetToTriage。
/// PR3 后续追加：RunSingle / RunBatch（驱动 IRuleEngineService 跑回归）/ ApproveAsExpected。
/// PR4 后续追加：IAiAdvisor 相关 — Triage 判定 / SuggestRule。
///
/// 乐观并发：Update / 三个状态切换接口都要求客户端携带最新 RowVersion；不一致返 1000「请刷新后重试」。
/// </remarks>
public interface IParseTestCaseService
{
    Task<ParseTestCaseListResponse> ListAsync(ParseTestCaseListQuery query, CancellationToken ct = default);

    Task<ParseTestCaseResponse> GetByIdAsync(long id, CancellationToken ct = default);

    Task<ParseTestCaseResponse> CreateAsync(CreateParseTestCaseRequest req, CancellationToken ct = default);

    Task<ParseTestCaseResponse> UpdateAsync(UpdateParseTestCaseRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteParseTestCaseRequest req, CancellationToken ct = default);

    /// <summary>从 Media_Item.Status=Failed 历史灌入暂存区（Status=PendingTriage）</summary>
    Task<ImportFromFailedResult> ImportFromFailedAsync(ImportFromFailedRequest req, CancellationToken ct = default);

    /// <summary>暂存区 → 正式样本（PendingTriage → Active）</summary>
    Task<ParseTestCaseResponse> PromoteToActiveAsync(TransitionParseTestCaseStatusRequest req, CancellationToken ct = default);

    /// <summary>停用（Active / PendingTriage → Disabled）</summary>
    Task<ParseTestCaseResponse> DisableAsync(TransitionParseTestCaseStatusRequest req, CancellationToken ct = default);

    /// <summary>回退到暂存区（Active / Disabled → PendingTriage）</summary>
    Task<ParseTestCaseResponse> ResetToTriageAsync(TransitionParseTestCaseStatusRequest req, CancellationToken ct = default);

    /// <summary>单条运行回归：调 IRuleEngineService 解析样本 → 写 LastRun* + 与 Expected* 比对得出 LastRunStatus</summary>
    Task<ParseTestCaseResponse> RunAsync(RunParseTestCaseRequest req, CancellationToken ct = default);

    /// <summary>批量运行回归：默认仅跑 Status=Active；返回 ran/pass/fail/notComparable 汇总</summary>
    Task<RunParseTestCasesBatchResult> RunBatchAsync(RunParseTestCasesBatchRequest req, CancellationToken ct = default);

    /// <summary>批准 LastRunResult 为期望基线（拷贝实测字段到 Expected* 并把 LastRunStatus 刷为 Pass）</summary>
    Task<ParseTestCaseResponse> ApproveAsExpectedAsync(ApproveAsExpectedRequest req, CancellationToken ct = default);

    /// <summary>交给 AI 判定该样本是否值得纳入测试集；结果写入 AiVerdict（JSON），不自动改 Status</summary>
    Task<ParseTestCaseResponse> TriageWithAiAsync(TriageWithAiRequest req, CancellationToken ct = default);

    /// <summary>请 AI 为该样本生成解析规则；pattern 写入 AiSuggestedRulePattern；返回完整建议供前端预填 Parse_Rule 表单</summary>
    Task<RuleSuggestionResponse> SuggestRuleAsync(SuggestRuleRequest req, CancellationToken ct = default);
}
