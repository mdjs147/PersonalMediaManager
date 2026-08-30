using PersonalMediaManager.Application.Dtos.Tmdb;

namespace PersonalMediaManager.Application.Services.Tmdb;

/// <summary>TMDB 配置服务契约（单例 Id=1）</summary>
/// <remarks>
/// 单例：Get 永远读 Id=1，Update 永远写 Id=1；不暴露 Create / Delete。
/// ApiKey 走 IProtectedFieldService 加密；响应仅暴露 HasApiKey 布尔。
/// /test 走 ITmdbConnectivityTester 真发 GET /3/configuration；/cache/clear 清两张缓存表并返回删除条数。
/// </remarks>
public interface ITmdbSettingService
{
    Task<TmdbSettingResponse> GetAsync(CancellationToken ct = default);

    Task<TmdbSettingResponse> UpdateAsync(UpdateTmdbSettingRequest req, CancellationToken ct = default);

    Task<TestTmdbResponse> TestAsync(CancellationToken ct = default);

    Task<ClearTmdbCacheResponse> ClearCacheAsync(CancellationToken ct = default);
}
