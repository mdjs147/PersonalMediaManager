using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>分类自动匹配规则（Category_MatchRule）</summary>
/// <remarks>
/// Conditions 列存 JSON 条件树（all/any + eq/in/contains/notEq/notIn，详见数据库设计 §1.11）；
/// EF ValueConverter 将 string 与 JsonNode/POCO 互转，序列化解析放 Application 层（保持 Domain 零依赖）。
/// CategoryId 走 CASCADE：删除 Category 时同时清规则。
/// 继承 AggregateRoot 以获得 RowVersion 乐观并发保护；前端编辑时须回传 RowVersion。
/// </remarks>
public sealed class CategoryMatchRule : AggregateRoot
{
    public long CategoryId { get; set; }

    public string? Name { get; set; }

    /// <summary>JSON 条件树（字符串原样存储，Application 层解析）</summary>
    public string Conditions { get; set; } = "{}";

    public int Priority { get; set; } = 100;

    public bool Enabled { get; set; } = true;
}
