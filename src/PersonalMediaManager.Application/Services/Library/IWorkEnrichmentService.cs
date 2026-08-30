namespace PersonalMediaManager.Application.Services.Library;

/// <summary>媒体作品富化服务契约 — 拉 TMDB 富化数据写 Media_Work + 关联表</summary>
/// <remarks>
/// 与解析/归档热路径解耦：作品详情打开时惰性富化单部（EnrichAsync），人工「整库刷新」批量回填（EnrichAllAsync），
/// 分季分集按需惰性拉取（EnsureSeasonEpisodesAsync）。维度实体（人员/类型/公司/电视台/关键词）按 TMDB id upsert 共享。
/// </remarks>
public interface IWorkEnrichmentService
{
    /// <summary>富化单部作品（命中 Media_Work.EnrichedAt 未过期且非 force 则跳过）</summary>
    /// <remarks>作品不存在则按 TMDB 详情创建；返回是否实际发起了富化（false=命中缓存跳过）。</remarks>
    Task<bool> EnrichAsync(int tmdbId, string mediaType, bool force, CancellationToken ct = default);

    /// <summary>批量富化全部已归档作品（手动整库刷新，供库内搜索覆盖历史作品）；返回处理作品数</summary>
    Task<int> EnrichAllAsync(CancellationToken ct = default);

    /// <summary>确保某剧集季的分集已缓存（首次展开惰性拉取 /tv/{id}/season/{n}）</summary>
    /// <remarks>作品未富化（Media_Work 不存在）时先 EnrichAsync 再拉季；非剧集直接返回。</remarks>
    Task EnsureSeasonEpisodesAsync(int tmdbId, string mediaType, int seasonNumber, bool force, CancellationToken ct = default);
}
