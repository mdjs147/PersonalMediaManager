namespace PersonalMediaManager.Application.Contracts;

/// <summary>TMDB 图片按需缓存代理契约（背景图 / 人物照 / 剧照 / Logo）</summary>
/// <remarks>
/// 与海报本地缓存（IPosterDownloader）同隐私模型：浏览器不直连 TMDB CDN，统一经后端代理 + 落盘缓存。
/// 海报仍走既有 /library/poster/{tmdbId}；本代理覆盖其余按 TMDB 相对路径取的图片。
/// </remarks>
public interface ITmdbImageProxy
{
    /// <summary>确保指定尺寸的 TMDB 图片已缓存到本地，返回本地文件绝对路径；非法入参或拉取失败返回 null</summary>
    Task<string?> GetCachedImageAsync(string size, string path, CancellationToken ct = default);
}
