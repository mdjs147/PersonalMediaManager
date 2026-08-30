using PersonalMediaManager.Application.Dtos.Category;

namespace PersonalMediaManager.Application.Services.Category;

/// <summary>分类匹配规则服务契约（Category_MatchRule CRUD）</summary>
/// <remarks>
/// Conditions 必须是合法 JSON（保存时 JsonDocument.Parse 校验）；
/// CategoryId 必须存在；Priority 越小越先匹配（D 阶段 ClassifyService 使用）。
/// </remarks>
public interface ICategoryMatchRuleService
{
    Task<IReadOnlyList<CategoryMatchRuleResponse>> ListAsync(long? categoryId, CancellationToken ct = default);

    Task<CategoryMatchRuleResponse> CreateAsync(CreateCategoryMatchRuleRequest req, CancellationToken ct = default);

    Task<CategoryMatchRuleResponse> UpdateAsync(UpdateCategoryMatchRuleRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteCategoryMatchRuleRequest req, CancellationToken ct = default);
}
