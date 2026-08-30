using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

/// <summary>从 System_Setting 读归档命名模板配置（4 个 Archive_ key → NamingTemplateOptions）</summary>
/// <remarks>
/// 降级容错（与 ArchiveService 读 ConflictPolicy / MinFreeSpace 风格一致）：任一 key 缺失 / 空 / 校验失败，
/// 回退该项 NamingTemplateOptions.Default 并记 Warning，**绝不因模板配错阻断归档**——命名模板是锦上添花，
/// 配错只应退回 Plex 默认命名，而非让整条归档管道失败。校验复用 NamingTemplateRenderer.ValidateTemplate。
/// 4 个 key 必须与 SystemSettingConfig 种子 + GeneralSettingsService.ArchiveNaming 专属页常量 1:1 对齐。
/// </remarks>
internal static class ArchiveNamingTemplateReader
{
    /// <summary>电影目录名 + 文件名主体模板 key</summary>
    public const string MovieNameTemplateKey = "Archive_MovieNameTemplate";

    /// <summary>剧集根目录主体模板 key</summary>
    public const string TvShowFolderTemplateKey = "Archive_TvShowFolderTemplate";

    /// <summary>单集文件名主体模板 key</summary>
    public const string TvEpisodeNameTemplateKey = "Archive_TvEpisodeNameTemplate";

    /// <summary>季目录是否追加季标题后缀开关 key</summary>
    public const string SeasonFolderIncludeTitleKey = "Archive_SeasonFolderIncludeTitle";

    /// <summary>批量读 4 个 key 组装模板配置；非法 / 缺失项逐项回退 Default（不抛）</summary>
    public static async Task<NamingTemplateOptions> ReadAsync(PmmDbContext db, ILogger? logger, CancellationToken ct)
    {
        string[] keys = { MovieNameTemplateKey, TvShowFolderTemplateKey, TvEpisodeNameTemplateKey, SeasonFolderIncludeTitleKey };
        Dictionary<string, string?> rows = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        NamingTemplateOptions def = NamingTemplateOptions.Default;
        return new NamingTemplateOptions(
            ResolveTemplate(rows, MovieNameTemplateKey, def.MovieNameTemplate, NamingTemplateRenderer.TemplateKind.Movie, logger),
            ResolveTemplate(rows, TvShowFolderTemplateKey, def.TvShowFolderTemplate, NamingTemplateRenderer.TemplateKind.TvShowFolder, logger),
            ResolveTemplate(rows, TvEpisodeNameTemplateKey, def.TvEpisodeNameTemplate, NamingTemplateRenderer.TemplateKind.TvEpisode, logger),
            ResolveBool(rows, SeasonFolderIncludeTitleKey, def.SeasonFolderIncludeTitle));
    }

    /// <summary>取单项模板：缺失 / 空 / 校验失败 → 回退 fallback 并 Warning</summary>
    private static string ResolveTemplate(
        IReadOnlyDictionary<string, string?> rows, string key, string fallback,
        NamingTemplateRenderer.TemplateKind kind, ILogger? logger)
    {
        if (!rows.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return fallback; // 缺失 / 空：静默回退默认（首次启用前的常态，不算异常）

        string candidate = raw.Trim();
        NamingTemplateRenderer.ValidationOutcome outcome = NamingTemplateRenderer.ValidateTemplate(candidate, kind);
        if (outcome.IsValid)
            return candidate;

        logger?.LogWarning(
            "归档命名模板 {Key} 配置非法，本次归档回退默认模板「{Fallback}」：{Errors}",
            key, fallback, string.Join("；", outcome.Errors));
        return fallback;
    }

    /// <summary>取布尔开关：缺失 / 非法回退默认</summary>
    private static bool ResolveBool(IReadOnlyDictionary<string, string?> rows, string key, bool fallback)
    {
        if (rows.TryGetValue(key, out string? raw) && bool.TryParse(raw?.Trim(), out bool b))
            return b;
        return fallback;
    }
}
