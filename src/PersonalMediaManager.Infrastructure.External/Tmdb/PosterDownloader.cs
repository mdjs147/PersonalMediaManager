using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Tmdb;

/// <summary>TMDB 海报下载实现（D2.4）— 契约见 Application.Contracts.IPosterDownloader</summary>
/// <remarks>
/// 写入路径：{cacheRoot}/posters/{TmdbId}.jpg；目录由调用方在 AppPaths 派生（&lt;LocalAppData&gt;/cache/）。
/// 已存在则跳过（按文件存在判定，不校验大小或哈希；前端展示需要清缓存时走 /settings/tmdb/cache/clear）。
/// 来源 URL：https://image.tmdb.org/t/p/{size}/{posterPath}；默认 size=w500（性价比最高）。
/// </remarks>
internal sealed class PosterDownloader : IPosterDownloader
{
    private const string TmdbImageBase = "https://image.tmdb.org/t/p";
    public const string DefaultSize = "w500";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PosterDownloader> _logger;

    public PosterDownloader(IHttpClientFactory httpFactory, ILogger<PosterDownloader> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<PosterDownloadResult> DownloadAsync(int tmdbId, string posterPath, string cacheRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(posterPath))
            throw new TmdbClientException("posterPath 不能为空");
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new TmdbClientException("cacheRoot 不能为空");

        string dir = Path.Combine(cacheRoot, "posters");
        Directory.CreateDirectory(dir);
        string local = Path.Combine(dir, $"{tmdbId}.jpg");

        if (File.Exists(local))
        {
            long existing = new FileInfo(local).Length;
            _logger.LogDebug("海报已存在跳过：{LocalPath} ({Bytes} bytes)", local, existing);
            return new PosterDownloadResult(false, local, existing);
        }

        string url = $"{TmdbImageBase}/{DefaultSize}{NormalizePath(posterPath)}";
        HttpClient client = _httpFactory.CreateClient("TmdbPosterDownloader");
        client.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            using HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new TmdbClientException($"海报下载失败 HTTP {(int)resp.StatusCode}：{url}", (int)resp.StatusCode);

            await using FileStream fs = File.Create(local);
            await resp.Content.CopyToAsync(fs, ct);
            long bytes = fs.Length;
            return new PosterDownloadResult(true, local, bytes);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient 30s 超时抛 TaskCanceledException（OperationCanceledException 子类）但 ct 并未取消：
            // 在源头翻译为 TmdbClientException，避免「伪装取消」穿透到上游被当真取消处理（与 TmdbClient 策略一致）
            TryDeletePartialFile(local);
            throw new TmdbClientException($"海报下载超时（30s）：{url}", inner: ex);
        }
        catch
        {
            // 任何失败清理半成品缓存文件：残留的部分写入会被「已存在跳过」误判为完好海报，永久污染缓存
            TryDeletePartialFile(local);
            throw;
        }
    }

    /// <summary>容错删除下载失败遗留的半成品文件（清理失败仅吞掉，主异常更重要）</summary>
    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 吞 — 清理失败不掩盖主异常
        }
    }

    /// <summary>TMDB poster_path 以 '/' 开头；空校验由上层做</summary>
    private static string NormalizePath(string raw)
    {
        string t = raw.Trim();
        return t.StartsWith('/') ? t : "/" + t;
    }
}
