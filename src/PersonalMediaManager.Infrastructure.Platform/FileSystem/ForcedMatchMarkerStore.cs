using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>强制匹配标识读取实现 —— 自文件目录逐层上溯监控根，命中最近的 pmm.txt / .url / 文件名或文件夹名 {tmdb-NNN}</summary>
/// <remarks>
/// 读取顺序：每一层先看 pmm.txt（固定名、大小写不敏感），再看含 themoviedb.org 的 .url 快捷方式；
/// **文件所在目录这一层**额外先看文件名自身的 {tmdb-NNN} 标记，再看该层目录名标记；更外层只看目录名标记。
/// 命中即解析返回，越靠近文件的标识优先。
/// 优先级：pmm.txt &gt; .url &gt; 文件名标记 &gt; 文件夹名标记（近层整体先于远层）。pmm.txt / URL 信息更全
/// （自带类型 + 季 + 剧集组），文件名 / 文件夹名标记只锚定 id（类型 / 季 / 集仍由规则识别），故退居其后。
/// 监控根未知（手动扫描）时只看文件直接父目录，不无限上溯。
/// 任何 IO / 解析异常一律吞为 null + 记 Warning，绝不让标识问题打断主处理管线。
/// </remarks>
internal sealed class ForcedMatchMarkerStore : IForcedMatchMarkerStore
{
    /// <summary>标识文件固定名（大小写不敏感）</summary>
    private const string MarkerFileName = "pmm.txt";

    /// <summary>上溯层数硬上限：防御异常路径导致的过深遍历</summary>
    private const int MaxAscendLevels = 16;

    private readonly ILogger<ForcedMatchMarkerStore> _logger;

    public ForcedMatchMarkerStore(ILogger<ForcedMatchMarkerStore> logger) => _logger = logger;

    public async Task<ForcedMatchMarker?> TryReadAsync(FileParseContext ctx, CancellationToken ct = default)
    {
        // 首次迭代 = 文件直接所在目录；在该层额外认文件名自身的标记（文件名比目录名更具体，故先于目录名标记）
        bool atFileLevel = true;
        foreach (string dir in EnumerateDirectories(ctx))
        {
            ct.ThrowIfCancellationRequested();

            // ① pmm.txt（大小写不敏感）
            string? markerPath = FindFileIgnoreCase(dir, MarkerFileName);
            if (markerPath is not null)
            {
                ForcedMatchMarker? hit = await TryParseFileAsync(markerPath, ct);
                if (hit is not null)
                {
                    _logger.LogInformation(
                        "命中强制匹配标识 {File} → tmdbId={TmdbId}, type={Type}, season={Season}, episodeGroup={Eg}",
                        markerPath, hit.TmdbId, hit.MediaType, hit.Season?.ToString() ?? "-", hit.EpisodeGroupId ?? "-");
                    return hit;
                }
            }

            // ② .url 快捷方式（取首个含 themoviedb.org 的）
            ForcedMatchMarker? fromUrl = await TryParseUrlShortcutsAsync(dir, ct);
            if (fromUrl is not null)
            {
                _logger.LogInformation("命中强制匹配 .url 快捷方式（{Dir}）→ tmdbId={TmdbId}", dir, fromUrl.TmdbId);
                return fromUrl;
            }

            // ③ 文件名自身的 {tmdb-NNN} 标记（仅文件所在目录这一层查一次；含扩展名不影响匹配）
            if (atFileLevel)
            {
                ForcedMatchMarker? fromFile = ForcedMatchMarkerParser.ParseTmdbMarker(ctx.FileName);
                if (fromFile is not null)
                {
                    _logger.LogInformation("命中强制匹配文件名标记（{File}）→ tmdbId={TmdbId}（类型/季/集由规则识别）", ctx.FileName, fromFile.TmdbId);
                    return fromFile;
                }
                atFileLevel = false;
            }

            // ④ 文件夹名本身的 {tmdb-NNN} 标记（仅锚 id，类型 / 季 / 集仍由规则识别）
            ForcedMatchMarker? fromName = ForcedMatchMarkerParser.ParseTmdbMarker(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)));
            if (fromName is not null)
            {
                _logger.LogInformation("命中强制匹配文件夹名标记（{Dir}）→ tmdbId={TmdbId}（类型/季/集由规则识别）", dir, fromName.TmdbId);
                return fromName;
            }
        }
        return null;
    }

    /// <summary>自文件目录起逐层上溯到监控根（含）；监控根未知时只取直接父目录</summary>
    private static IEnumerable<string> EnumerateDirectories(FileParseContext ctx)
    {
        string? dir = SafeGetDirectoryName(ctx.FullPath);
        if (dir is null) yield break;

        string? root = string.IsNullOrWhiteSpace(ctx.WatchRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(ctx.WatchRoot);

        for (int level = 0; level < MaxAscendLevels && dir is not null; level++)
        {
            yield return dir;

            // 监控根未知：只看直接父目录，不继续上溯（避免逃逸到系统目录）
            if (root is null) yield break;
            // 已到监控根：到此为止（监控根自身已 yield）
            if (string.Equals(Path.TrimEndingDirectorySeparator(dir), root, StringComparison.OrdinalIgnoreCase))
                yield break;

            dir = SafeGetDirectoryName(dir);
        }
    }

    private async Task<ForcedMatchMarker?> TryParseFileAsync(string path, CancellationToken ct)
    {
        try
        {
            string content = await File.ReadAllTextAsync(path, ct);
            return ForcedMatchMarkerParser.Parse(content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取强制匹配标识失败（忽略，走正常解析）：{File}", path);
            return null;
        }
    }

    private async Task<ForcedMatchMarker?> TryParseUrlShortcutsAsync(string dir, CancellationToken ct)
    {
        IEnumerable<string> urlFiles;
        try
        {
            if (!Directory.Exists(dir)) return null;
            urlFiles = Directory.EnumerateFiles(dir, "*.url");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举 .url 快捷方式失败（忽略）：{Dir}", dir);
            return null;
        }

        foreach (string file in urlFiles)
        {
            ct.ThrowIfCancellationRequested();
            ForcedMatchMarker? hit = await TryParseFileAsync(file, ct);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>在目录内大小写不敏感查找指定文件名；返回真实路径或 null</summary>
    private static string? FindFileIgnoreCase(string dir, string fileName)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            // 先试精确路径（绝大多数命中走这条，零枚举开销）
            string direct = Path.Combine(dir, fileName);
            if (File.Exists(direct)) return direct;
            // Windows 文件系统本就大小写不敏感，精确命中即可；此处兜底跨平台/异常大小写
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch
        {
            // 目录不可读等 → 视作未命中
        }
        return null;
    }

    private static string? SafeGetDirectoryName(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch
        {
            return null;
        }
    }
}
