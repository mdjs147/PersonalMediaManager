using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

/// <summary>归档落点解析：标题规范化 + {tmdb-NNN} 目录唯一性复用</summary>
/// <remarks>
/// 实际归档（ArchiveService）与各预览（ReviewService 去向预览 / DryRunService 整理演练）共用本实现，
/// 从根上杜绝「预览 ≠ 实际落点」漂移：同一作品的标题段一律用 TMDB 规范名，目录按 {tmdb-NNN} 唯一复用。
/// 纯静态；ILogger 仅用于复用命中日志，预览侧无 logger 时传 null 即可。
/// </remarks>
internal static class ArchiveFolderResolver
{
    /// <summary>取首个非空白字符串；全空返回 null</summary>
    public static string? FirstNonBlank(params string?[] values)
    {
        foreach (string? v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    /// <summary>算归档相对路径：标题段用规范名；剧集根 / 电影目录按 {tmdb-NNN} 复用 targetRoot 下已存在目录</summary>
    /// <remarks>
    /// 剧集要求 season / episode 已由调用方按各自语义守护后传入（实际归档抛 BusinessException、预览返回 Error 项），
    /// 电影忽略二者。targetRootForReuse 为 null（如整理演练无分类根）时跳过复用扫描，仅做标题规范化。
    /// 标题清洗 / 年份越界等命名非法会从 PlexNamingConventions 抛 ArgumentException，由调用方决定吞或转人工。
    /// </remarks>
    public static string BuildRelativeTargetPath(
        string canonicalTitle, int year, int tmdbId, bool isTv,
        int season, int episode, int? episodeEnd, string extension,
        string? targetRootForReuse, ILogger? logger = null,
        int? seasonYear = null, string? seasonTitle = null,
        NamingTemplateOptions? template = null, string? originalTitle = null, string? edition = null)
    {
        // template 为 null（现有调用方 / 演练等）→ Default（与改造前逐字节一致），故新增参数不破坏既有行为
        NamingTemplateOptions opts = template ?? NamingTemplateOptions.Default;
        string ext = PlexNamingConventions.NormalizeExtension(extension);
        // 季目录是否带篇章名后缀由 opts.SeasonFolderIncludeTitle 控制：关则不向 SeasonFolder 传季标题（恒纯 Season NN）
        string? effectiveSeasonTitle = opts.SeasonFolderIncludeTitle ? seasonTitle : null;
        string canonicalShowFolder = isTv
            ? PlexNamingConventions.TvShowFolder(canonicalTitle, year, tmdbId, opts, originalTitle)
            : PlexNamingConventions.MovieFolder(canonicalTitle, year, tmdbId, opts, originalTitle);
        string showFolder = targetRootForReuse is null
            ? canonicalShowFolder
            : ResolveShowFolder(targetRootForReuse, tmdbId, canonicalShowFolder, logger);

        if (isTv)
        {
            // 多季差异化：季目录带季标题、季内单集文件名用该季首播年（seasonYear 缺失回退整剧 year）；
            // 剧集根目录 showFolder 仍用整剧 year（Plex 按 {tmdb-NNN}+根目录唯一识别全剧，年份不按季漂移）。
            return Path.Combine(
                showFolder,
                PlexNamingConventions.SeasonFolder(season, effectiveSeasonTitle),
                $"{PlexNamingConventions.TvEpisodeFileBase(canonicalTitle, seasonYear ?? year, season, episode, episodeEnd, opts, originalTitle)}.{ext}");
        }
        return Path.Combine(showFolder, $"{PlexNamingConventions.MovieFileBase(canonicalTitle, year, edition, tmdbId, opts, originalTitle)}.{ext}");
    }

    /// <summary>从 TMDB 缓存原始响应 + 文件解析篇章名算季文件夹后缀：(该季首播年, 归一化季标题)</summary>
    /// <remarks>
    /// 复用 TmdbSeasonsParser（与 TMDB 详情、审核页同一口径）解析 seasons[]，按季号取 air_date 年 + 季名；
    /// 季标题经 NormalizeSeasonTitle：TMDB 季名（过滤默认季名）优先，回退文件解析篇章名。
    /// rawJson 为空 / 无该季 → (null, NormalizeSeasonTitle(null, parsedSeasonTitle))：季年份回退、季标题仅看解析篇章名。
    /// 归档（meta.RawJson）与去向预览（缓存 RawJson）共用；整理演练无 rawJson 时仅靠 parsedSeasonTitle。
    /// </remarks>
    public static (int? SeasonYear, string? SeasonTitle) ResolveSeasonNaming(string? rawJson, string? parsedSeasonTitle, int season)
    {
        TmdbSeasonInfo? si = TmdbSeasonsParser.Parse(rawJson).FirstOrDefault(s => s.SeasonNumber == season);
        return (si?.Year, PlexNamingConventions.NormalizeSeasonTitle(si?.Name, parsedSeasonTitle));
    }

    /// <summary>按 {tmdb-NNN} 唯一性解析剧集根 / 电影目录名：分类根下已存在同 tmdbId 标记的目录则复用，否则用规范名</summary>
    /// <remarks>
    /// 「同一作品唯一文件夹」的兜底闸：即便本次标题段（中英 / 大小写 / 篇章副标题）与历史目录不同，
    /// 只要 {tmdb-NNN} 命中即落入已存在目录，杜绝散落。标记匹配用 OrdinalIgnoreCase，兼容历史目录里
    /// 大写 {TMDB-NNN} 写法；闭合花括号天然防前缀粘连（{tmdb-1} 不会命中 {tmdb-1396}）。
    /// 命中多个（历史散落）时取确定性的一个：优先与规范名精确相等者，否则字典序最小，保证后续文件落点稳定。
    /// 目录扫描失败（权限 / 网络盘未就绪）或分类根不存在 → 回退规范名新建，绝不因扫描阻断归档。
    /// 仅扫描分类根的直接子目录（Plex 库每部作品一个顶层文件夹），不递归。
    /// </remarks>
    public static string ResolveShowFolder(string targetRoot, int tmdbId, string canonicalShowFolder, ILogger? logger = null)
    {
        string marker = $"{{tmdb-{tmdbId}}}";
        try
        {
            if (!Directory.Exists(targetRoot)) return canonicalShowFolder;
            List<string> matches = new();
            foreach (string dir in Directory.EnumerateDirectories(targetRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    matches.Add(name);
            }
            if (matches.Count == 0) return canonicalShowFolder;

            string chosen = matches.Contains(canonicalShowFolder, StringComparer.Ordinal)
                ? canonicalShowFolder
                : matches.OrderBy(n => n, StringComparer.Ordinal).First();
            if (!string.Equals(chosen, canonicalShowFolder, StringComparison.Ordinal))
                logger?.LogInformation(
                    "按 tmdbId 复用已存在目录（标题段与规范名不同）：tmdb-{TmdbId} → {Chosen}（规范名应为 {Canonical}）",
                    tmdbId, chosen, canonicalShowFolder);
            return chosen;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "扫描分类根目录复用失败，回退规范名新建：{Root}（tmdb-{TmdbId}）", targetRoot, tmdbId);
            return canonicalShowFolder;
        }
    }
}
