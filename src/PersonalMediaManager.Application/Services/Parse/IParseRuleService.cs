using PersonalMediaManager.Application.Dtos.Parse;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>解析规则服务契约（Parse_Rule CRUD + 即席测试）</summary>
/// <remarks>
/// 保存前用 Regex(Compiled, Timeout=500ms) 试编译 Pattern，编译失败 → 1000；
/// /test 端点不读 DB，传入 pattern + sample 即席匹配，返回命名捕获组与耗时。
/// </remarks>
public interface IParseRuleService
{
    Task<IReadOnlyList<ParseRuleResponse>> ListAsync(CancellationToken ct = default);

    Task<ParseRuleResponse> CreateAsync(CreateParseRuleRequest req, CancellationToken ct = default);

    Task<ParseRuleResponse> UpdateAsync(UpdateParseRuleRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteParseRuleRequest req, CancellationToken ct = default);

    Task<TestParseRuleResponse> TestAsync(TestParseRuleRequest req, CancellationToken ct = default);

    /// <summary>从 JSON 流批量导入规则；mode=Replace 时先清空再插入，Merge 时按 Name 去重跳过已存在</summary>
    Task<ImportParseRulesResult> ImportAsync(Stream input, ParseRuleImportMode mode, CancellationToken ct = default);

    /// <summary>列出 RuleEngineService 内置正则（用户规则之后兜底运行，仅用于 UI 展示，不可编辑）</summary>
    IReadOnlyList<BuiltinParseRuleResponse> ListBuiltins();
}
