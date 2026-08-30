namespace PersonalMediaManager.Application.Contracts;

/// <summary>TMDB 海报下载契约（D2.4）</summary>
/// <remarks>
/// 写入路径：{cacheRoot}/posters/{TmdbId}.jpg；cacheRoot 由调用方从 AppPaths.CacheDir 派生（&lt;LocalAppData&gt;/PersonalMediaManager/cache）。
/// 已存在则跳过（按文件存在判定，不校验大小或哈希；需要刷新走 /settings/tmdb/cache/clear）。
/// 来源 URL：https://image.tmdb.org/t/p/{size}/{posterPath}；默认 size=w500。
/// 契约置于 Application.Contracts（而非 Infrastructure.External）：让 Infrastructure.Persistence 的
/// TmdbSearchService / ArchiveService 可依赖海报下载而不跨 Infrastructure 子项目互引（§0.5 红线）；实现仍在 External。
/// </remarks>
public interface IPosterDownloader
{
    Task<PosterDownloadResult> DownloadAsync(int tmdbId, string posterPath, string cacheRoot, CancellationToken ct = default);
}

/// <summary>海报下载结果（是否真正下载 / 本地绝对路径 / 写入字节数）</summary>
public sealed record PosterDownloadResult(bool Downloaded, string LocalPath, long BytesWritten);
