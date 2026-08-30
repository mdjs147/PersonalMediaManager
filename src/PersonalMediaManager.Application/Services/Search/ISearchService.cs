using PersonalMediaManager.Application.Dtos.Search;

namespace PersonalMediaManager.Application.Services.Search;

/// <summary>统一全局搜索服务 — 历史 / 媒体库 / 待确认三域聚合</summary>
/// <remarks>
/// 顶栏搜索框入口：一次调用串行查三个域（单 DbContext，禁 Task.WhenAll），各域返回 topN 命中 + 真实总数。
/// 关键词空 / 纯空白时直接返回空结果（不报错、不触发任何查询）。
/// </remarks>
public interface ISearchService
{
    /// <summary>聚合查询历史 / 媒体库 / 待确认命中（按 Scopes 选域，缺省全查）</summary>
    Task<GlobalSearchResponse> ListAsync(GlobalSearchQuery query, CancellationToken ct = default);
}
