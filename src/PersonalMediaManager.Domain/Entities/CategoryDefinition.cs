using PersonalMediaManager.Domain.Common;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Domain.Entities;

/// <summary>媒体分类定义（Category_Definition）</summary>
/// <remarks>
/// Name 全局唯一；MediaType=Both 仅本表使用（如纪录片可既归类电影也归类剧集），Media_Item 不允许 Both。
/// 删除分类前必须无 Media_Item 引用，否则 C.3 应用服务返 1000；级联：Category_MatchRule 自动 CASCADE。
/// 继承 AggregateRoot 以获得 RowVersion 乐观并发保护；前端编辑时须回传 RowVersion。
/// </remarks>
public sealed class CategoryDefinition : AggregateRoot
{
    public string Name { get; set; } = default!;

    public MediaType MediaType { get; set; }

    /// <summary>目标根目录绝对路径</summary>
    public string TargetRoot { get; set; } = default!;

    public int Priority { get; set; } = 100;

    public string? Description { get; set; }
}
