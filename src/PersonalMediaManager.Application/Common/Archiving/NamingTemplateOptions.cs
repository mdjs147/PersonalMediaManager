namespace PersonalMediaManager.Application.Common.Archiving;

/// <summary>归档命名模板配置：3 个文件名主体模板 + 1 个季标题开关</summary>
/// <remarks>
/// 安全边界（与刮削必需的硬结构解耦）：模板只控制「{tmdb-NNN} 后缀之前的标题主体」，
/// 而 {tmdb-NNN} 锚点、Season NN 季目录、SxxEyy 集号、(year) 圆括号年份、伴随文件名（tvshow.nfo / poster.jpg）
/// 一律由代码锁死，模板无权改动（详见 NamingTemplateRenderer）。
///
/// 占位符白名单（每个占位符的值在渲染前单独过 PlexNamingConventions.Sanitize）：
///   {title} {originaltitle} {year} {tmdbid} {edition}（仅电影）{episode}（剧集强制）{seasonyear}（剧集）。
/// 三模板默认值 == 改造前硬编码命名，故 Default 渲染与历史归档逐字节一致（升级零行为变化）。
///
/// SeasonFolderIncludeTitle：季目录是否追加季标题后缀（如「Season 03 锻刀村篇」）；false 则恒为纯「Season NN」。
/// 季目录名本身不开放模板（Plex / Emby / Jellyfin 靠 Season NN 前缀识别季），仅此开关控制是否带篇章名后缀。
/// </remarks>
public sealed record NamingTemplateOptions(
    string MovieNameTemplate,
    string TvShowFolderTemplate,
    string TvEpisodeNameTemplate,
    bool SeasonFolderIncludeTitle)
{
    /// <summary>电影目录名 + 电影文件名主体默认模板（同源；代码末尾再强制追加 {tmdb-NNN} 与 .ext）</summary>
    public const string DefaultMovieNameTemplate = "{title} ({year})";

    /// <summary>剧集根目录主体默认模板（代码末尾强制追加 {tmdb-NNN}）</summary>
    public const string DefaultTvShowFolderTemplate = "{title} ({year})";

    /// <summary>单集文件名主体默认模板（{episode} 强制存在 = SxxEyy / 范围 SxxEaa-Ebb）</summary>
    public const string DefaultTvEpisodeNameTemplate = "{title} ({year}) - {episode}";

    /// <summary>默认配置：三模板 == 改造前硬编码命名，季标题后缀开启（与历史行为一致）</summary>
    public static readonly NamingTemplateOptions Default = new(
        DefaultMovieNameTemplate,
        DefaultTvShowFolderTemplate,
        DefaultTvEpisodeNameTemplate,
        SeasonFolderIncludeTitle: true);
}
