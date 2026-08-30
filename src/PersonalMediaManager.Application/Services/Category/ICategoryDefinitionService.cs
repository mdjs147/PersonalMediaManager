using PersonalMediaManager.Application.Dtos.Category;

namespace PersonalMediaManager.Application.Services.Category;

/// <summary>分类定义服务契约（Category_Definition CRUD）</summary>
/// <remarks>
/// Name 全局唯一；删除前必须无 Media_Item 引用（→ 1000）；
/// 删除分类时 Category_MatchRule 由 DB 级联清理（FK ON DELETE CASCADE）。
/// </remarks>
public interface ICategoryDefinitionService
{
    Task<IReadOnlyList<CategoryDefinitionResponse>> ListAsync(CancellationToken ct = default);

    Task<CategoryDefinitionResponse> CreateAsync(CreateCategoryDefinitionRequest req, CancellationToken ct = default);

    Task<CategoryDefinitionResponse> UpdateAsync(UpdateCategoryDefinitionRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteCategoryDefinitionRequest req, CancellationToken ct = default);
}
