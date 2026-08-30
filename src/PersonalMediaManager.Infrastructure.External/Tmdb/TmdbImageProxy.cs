using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Tmdb;

/// <summary>TMDB 图片按需缓存代理实现</summary>
/// <remarks>
/// 走 TMDB 系命名 client（受 Proxy_UseForTmdb 控制）；缓存到 AppPaths.CacheDir/tmdb-images/{size}/{file}。
/// TMDB 图片名按内容哈希命名（同 path 内容不变），故文件存在即直接复用、无需 TTL。
/// 尺寸白名单 + 文件名清洗（禁路径穿越）防滥用。
/// </remarks>
internal sealed class TmdbImageProxy : ITmdbImageProxy
{
    public const string HttpClientName = "TmdbImageProxy";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    private static readonly HashSet<string> AllowedSizes = new(StringComparer.Ordinal)
    {
        "w45", "w92", "w154", "w185", "w300", "w342", "w500", "w780", "w1280", "h632", "original",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly AppPaths _paths;
    private readonly ILogger<TmdbImageProxy> _logger;

    public TmdbImageProxy(IHttpClientFactory httpFactory, AppPaths paths, ILogger<TmdbImageProxy> logger)
    {
        _httpFactory = httpFactory;
        _paths = paths;
        _logger = logger;
    }

    public async Task<string?> GetCachedImageAsync(string size, string path, CancellationToken ct = default)
    {
        if (!AllowedSizes.Contains(size)) return null;
        string? safe = SanitizeImagePath(path);
        if (safe is null) return null;

        string dir = Path.Combine(_paths.CacheDir, "tmdb-images", size);
        string file = Path.Combine(dir, safe);
        if (File.Exists(file)) return file;

        try
        {
            Directory.CreateDirectory(dir);
            HttpClient client = _httpFactory.CreateClient(HttpClientName);
            using HttpResponseMessage resp = await client.GetAsync($"{ImageBase}{size}/{safe}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            // 先写临时文件再原子改名，避免并发/中断留下半截文件
            string tmp = file + ".tmp";
            await using (FileStream fs = File.Create(tmp))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB 图片代理失败：size={Size} path={Path}", size, path);
            return null;
        }
    }

    /// <summary>清洗图片相对路径：TMDB 图片名形如 abcDEF.jpg（单段），仅允许字母数字 . _ -</summary>
    private static string? SanitizeImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.TrimStart('/');
        if (p.Length is 0 or > 128) return null;
        if (p.Contains("..") || p.Contains('/') || p.Contains('\\')) return null;
        foreach (char c in p)
        {
            if (!(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) return null;
        }
        return p;
    }
}
