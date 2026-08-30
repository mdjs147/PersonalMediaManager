using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Parse;

/// <summary>解析规则响应 DTO</summary>
/// <remarks>RowVersion 给前端用于乐观并发：Update 时回传，服务端比对不一致则 1000「请刷新后重试」</remarks>
public sealed record ParseRuleResponse(
    long Id,
    string Name,
    ParseScope Scope,
    string Pattern,
    string? DefaultType,
    bool ForceType,
    int Priority,
    double ConfidenceBonus,
    bool Enabled,
    string? Description,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateParseRuleRequest(
    [RequiredNotBlank(ErrorMessage = "规则 Name 不能为空")]
    [MaxLength(100, ErrorMessage = "规则 Name 长度不能超过 100 字符")]
    string Name,
    ParseScope Scope,
    [RequiredNotBlank(ErrorMessage = "Pattern 不能为空")]
    [MaxLength(1000, ErrorMessage = "Pattern 长度不能超过 1000 字符")]
    string Pattern,
    string? DefaultType = null,
    bool ForceType = false,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority = 100,
    [Range(0.0, 0.3, ErrorMessage = "ConfidenceBonus 必须在 [0, 0.3] 范围内")]
    double ConfidenceBonus = 0.0,
    bool Enabled = true,
    [MaxLength(500, ErrorMessage = "Description 长度不能超过 500 字符")]
    string? Description = null);

public sealed record UpdateParseRuleRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "规则 Name 不能为空")]
    [MaxLength(100, ErrorMessage = "规则 Name 长度不能超过 100 字符")]
    string Name,
    ParseScope Scope,
    [RequiredNotBlank(ErrorMessage = "Pattern 不能为空")]
    [MaxLength(1000, ErrorMessage = "Pattern 长度不能超过 1000 字符")]
    string Pattern,
    string? DefaultType,
    bool ForceType,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority,
    [Range(0.0, 0.3, ErrorMessage = "ConfidenceBonus 必须在 [0, 0.3] 范围内")]
    double ConfidenceBonus,
    bool Enabled,
    [MaxLength(500, ErrorMessage = "Description 长度不能超过 500 字符")]
    string? Description,
    long RowVersion);

public sealed record DeleteParseRuleRequest(long Id);

/// <summary>/test 端点请求：用 pattern + sample 即席测试</summary>
public sealed record TestParseRuleRequest(string Pattern, string Sample);

/// <summary>命中字段：命名捕获组名 → 捕获值</summary>
public sealed record TestParseRuleResponse(
    bool Matched,
    IReadOnlyDictionary<string, string> Groups,
    double ElapsedMilliseconds,
    string? ErrorMessage);

/// <summary>导入冲突策略</summary>
public enum ParseRuleImportMode
{
    /// <summary>合并：按 Name 去重，已存在则跳过；新增插入</summary>
    Merge = 0,
    /// <summary>替换：先清空所有规则再插入（高风险）</summary>
    Replace = 1,
}

/// <summary>单条导入项（与 Create 同字段）</summary>
public sealed record ImportParseRuleItem(
    string Name,
    ParseScope Scope,
    string Pattern,
    string? DefaultType = null,
    bool ForceType = false,
    int Priority = 100,
    double ConfidenceBonus = 0.0,
    bool Enabled = true,
    string? Description = null);

/// <summary>导入失败明细</summary>
public sealed record ImportParseRuleFailure(int Index, string? Name, string Message);

/// <summary>导入结果（部分失败也返 200）</summary>
public sealed record ImportParseRulesResult(
    int Added,
    int Skipped,
    int DeletedBeforeImport,
    IReadOnlyList<ImportParseRuleFailure> Failures);

/// <summary>内置规则展示项（只读，纯静态数据）</summary>
/// <remarks>
/// 内置规则是 RuleEngineService 在所有用户规则之后兜底运行的一组静态正则，
/// 不存在数据库、不可编辑、不可禁用；本响应仅用于 UI 展示让用户感知到内置规则的存在。
/// Order 字段按 RuleEngineService 内部代码顺序编号（季集 → 中文集号 → 年份 → 清洗类），
/// 前端按 Order 升序渲染。
/// </remarks>
public sealed record BuiltinParseRuleResponse(
    string Key,
    string Name,
    string Description,
    string Pattern,
    int Order,
    IReadOnlyList<string> Samples);
