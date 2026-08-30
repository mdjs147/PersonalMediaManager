using PersonalMediaManager.Application.Dtos.Watch;

namespace PersonalMediaManager.Application.Services.Watch;

/// <summary>忽略规则服务契约（Watch_IgnoreRule CRUD）</summary>
/// <remarks>
/// Extension 类型的 Pattern 必须以 '.' 开头并归一化为小写；同 Type 下 Pattern 不可重复（DB UQ + 服务前置校验）。
/// </remarks>
public interface IWatchIgnoreRuleService
{
    Task<IReadOnlyList<WatchIgnoreRuleResponse>> ListAsync(CancellationToken ct = default);

    Task<WatchIgnoreRuleResponse> CreateAsync(CreateWatchIgnoreRuleRequest req, CancellationToken ct = default);

    Task<WatchIgnoreRuleResponse> UpdateAsync(UpdateWatchIgnoreRuleRequest req, CancellationToken ct = default);

    Task DeleteAsync(DeleteWatchIgnoreRuleRequest req, CancellationToken ct = default);
}
