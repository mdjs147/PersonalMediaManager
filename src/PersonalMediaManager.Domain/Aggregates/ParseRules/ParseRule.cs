using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Aggregates.ParseRules;

/// <summary>解析规则聚合根（Parse_Rule）</summary>
/// <remarks>
/// 阶段 A：仅字段；阶段 C/D 在 Application 层加载到内存后按 (Enabled, Priority) 排序 + 编译为
/// Regex(... RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))。
/// 推荐命名捕获 title / year / season / episode；ForceType=true 命中后强制覆盖类型。
/// </remarks>
public sealed class ParseRule : AggregateRoot
{
    public string Name { get; set; } = default!;

    public ParseScope Scope { get; set; } = ParseScope.FileName;

    /// <summary>.NET 正则；推荐命名捕获 title/year/season/episode</summary>
    public string Pattern { get; set; } = default!;

    /// <summary>movie / tv / NULL（按捕获推断）</summary>
    public string? DefaultType { get; set; }

    /// <summary>命中后强制覆盖类型</summary>
    public bool ForceType { get; set; }

    public int Priority { get; set; } = 100;

    /// <summary>命中后追加置信度（0~0.3）</summary>
    public double ConfidenceBonus { get; set; }

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }
}
