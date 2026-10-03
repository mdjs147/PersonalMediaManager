using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.Review;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Review;

internal sealed partial class ReviewService
{
    // 同一批确认中同一作品及季仅刷新一次；服务为 scoped，不跨请求持有用户状态。
    private readonly Dictionary<(int Id, string Type), TmdbDetailsResult> _reviewDetails = [];
    private readonly Dictionary<(int Id, string Type, int? Season), ReviewMetadataState> _reviewMetadata = [];
    private sealed record ReviewMetadataState(TmdbSeasonCatalogueResult? Catalogue, string? Error);

    private async Task<TmdbDetailsResult> GetReviewDetailsAsync(int id, string type, bool force, CancellationToken ct, bool requireKnown = true)
    {
        if (id <= 0) throw new BusinessException("TMDB ID 必须为正整数");
        string mt = type.ToLowerInvariant();
        if (force && _reviewDetails.TryGetValue((id, mt), out TmdbDetailsResult? existing))
        {
            if (requireKnown && existing.CachedAt is null && !string.IsNullOrEmpty(existing.RefreshError))
                throw new BusinessException("TMDB 刷新失败且没有有效缓存，暂不能确认该作品");
            return existing;
        }
        // 默认接口实现与旧测试替身兼容；实际实现始终返回带缓存来源的结果。
        TmdbDetailsResult? result = await _tmdb.GetDetailsFreshAsync(id, mt, force, ct);
        result ??= await _tmdb.GetDetailsAsync(id, mt, ct);
        if (result is null || result.TmdbId != id || result.MediaType != mt)
            throw new BusinessException("TMDB 详情与所选作品类型及 ID 不一致");
        if (requireKnown && result.CachedAt is null && !string.IsNullOrEmpty(result.RefreshError))
            throw new BusinessException("TMDB 刷新失败且没有有效缓存，暂不能确认该作品");
        if (force) _reviewDetails[(id, mt)] = result;
        return result;
    }

    private async Task<ReviewMetadataState> RefreshSelectedMetadataAsync(TmdbDetailsResult details, int? season, bool force, CancellationToken ct)
    {
        (int Id, string Type, int? Season) key = (details.TmdbId, details.MediaType, season);
        if (force && _reviewMetadata.TryGetValue(key, out ReviewMetadataState? cached)) return cached;
        TmdbSeasonCatalogueResult? catalogue = null;
        string? error = details.RefreshError;
        if (details.MediaType == "tv" && season is int sn)
        {
            if (sn < 0) throw new BusinessException("季号不能为负数");
            catalogue = await _tmdb.GetSeasonCatalogueAsync(details.TmdbId, sn, force, ct);
            error ??= catalogue?.RefreshError;
        }
        // 用户确认或显式刷新时同步媒体库，防止基础缓存更新而库内集介绍仍永久停留在旧版本。
        if (force && _enrichment is not null)
        {
            try
            {
                await _enrichment.EnrichAsync(details.TmdbId, details.MediaType, force: true, ct);
                if (details.MediaType == "tv" && season is int selected)
                    await _enrichment.EnsureSeasonEpisodesAsync(details.TmdbId, "tv", selected, force: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                error ??= "媒体库元数据刷新失败，已保留已有资料，可稍后重试";
                _logger.LogWarning(ex, "人工所选元数据刷新失败：TmdbId={TmdbId} Type={Type} Season={Season}", details.TmdbId, details.MediaType, season);
            }
        }
        ParseDiagnostics.Emit("manual.metadata_refreshed", new
        {
            source = "TmdbCatalogue", details.TmdbId, details.MediaType, season,
            details.CachedAt, details.FromCache, error,
            catalogueCachedAt = catalogue?.CachedAt, catalogueFromCache = catalogue?.FromCache,
            episodeCount = catalogue?.Season?.Episodes.Count, forceRefresh = force,
        });
        ReviewMetadataState state = new(catalogue, error);
        if (force) _reviewMetadata[key] = state;
        return state;
    }

    private static string ValidateDecisionSource(string? source)
        => source is "ManualForm" or "RuleExtraction" or "BatchFill" or "AbsoluteMapping" or "LibrarySelection"
            ? source : throw new BusinessException("人工填写来源无效，请刷新页面后重试");

    private async Task<ReviewEpisodeMappingEntry?> ValidateConfirmedMappingAsync(long id, ConfirmRequest req, CancellationToken ct)
    {
        if (req.DecisionSource != "AbsoluteMapping") return null;
        if (req.SourceEpisode is null || req.Season is null || string.IsNullOrEmpty(req.MappingToken))
            throw new BusinessException("累计编号映射缺少原编号或预览依据，请重新预览并确认");
        ReviewEpisodeMappingResult result = await PreviewEpisodeMappingAsync(new(req.TmdbId, req.MediaType, req.Season.Value,
            [new(id, req.RowVersion, req.SourceEpisode.Value, req.SourceEpisodeEnd)]), ct);
        ReviewEpisodeMappingEntry mapped = result.Items.Single();
        if (mapped.Error is not null || mapped.MappingToken != req.MappingToken || mapped.Season != req.Season
            || mapped.Episode != req.Episode || mapped.EpisodeEnd != req.EpisodeEnd)
            throw new BusinessException(mapped.Error ?? "编号或目录依据已变，请重新预览并确认");
        return mapped;
    }
}
