using System.Text;
using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Common.Archiving;

/// <summary>Plex 命名规范（D4.2）— 电影 / 剧集 / 特别篇 / 多版本（需求文档 §3.6.1~§3.6.5）</summary>
/// <remarks>
/// 纯逻辑、无 IO；输入 = 标题/年份/季集/扩展名 + 类型 → 输出 = 目标相对路径段（不含分类根目录）。
/// 路径分隔符：用 Path.DirectorySeparatorChar（Windows 即反斜杠）。
/// 文件名清洗：禁用字符 &lt; &gt; : " / \ | ? * 替换为下划线（Windows 严格集合，已涵盖 POSIX 必禁的 / + NUL）。
/// 标题 trim 末尾点 + 空格（Windows 不允许文件名以点或空格结尾）。
///
/// 多版本：Edition 后缀放在文件名末尾「年份 - Edition」格式（Plex 官方支持的 EditionName 解析模式）。
/// 特别篇：Season 00 + S00E{NN}，与剧集统一公式（只是季号特化）。
/// 集号下界 = 0：动漫前导集 / 第 0 话是合法集号，生成 SxxE00（与 Season 00 同口径，非「解析失败」）。
/// TmdbId 标记：电影目录 + 电影文件名、剧集根目录可选追加「 {tmdb-NNN}」（Plex 官方匹配标记，方便强制匹配），
/// 季目录与单集文件名不带标记；tmdbId 传 null 或 ≤0 时不追加，行为同改造前。
/// </remarks>
public static class PlexNamingConventions
{
    /// <summary>电影目录段：「{清洗标题} ({年份})」，tmdbId 非空追加「 {tmdb-NNN}」</summary>
    public static string MovieFolder(string title, int year, int? tmdbId = null)
        => MovieFolder(title, year, tmdbId, NamingTemplateOptions.Default, null);

    /// <summary>电影目录段（模板化）：按 options.MovieNameTemplate 渲染主体，再追加 {tmdb-NNN}（目录不带 edition）</summary>
    /// <remarks>
    /// 目录名与文件名主体同源（同一模板），但目录名**不含 edition**（多版本只区分文件，目录唯一）。
    /// 渲染值（title/originaltitle/...）已在内部过 Sanitize；模板字面量自带圆括号等修饰。
    /// </remarks>
    public static string MovieFolder(string title, int year, int? tmdbId, NamingTemplateOptions options, string? originalTitle)
    {
        ValidateTitle(title);
        ValidateYear(year);
        string body = NamingTemplateRenderer.RenderToken(options.MovieNameTemplate, MovieTokens(title, year, tmdbId, originalTitle, edition: null));
        return $"{body}{TmdbSuffix(tmdbId)}";
    }

    /// <summary>电影文件名（不含扩展名）：「{清洗标题} ({年份})」[ - {版本}]，tmdbId 非空追加「 {tmdb-NNN}」（置于末尾）</summary>
    public static string MovieFileBase(string title, int year, string? edition = null, int? tmdbId = null)
        => MovieFileBase(title, year, edition, tmdbId, NamingTemplateOptions.Default, null);

    /// <summary>电影文件名主体（模板化）：按 options.MovieNameTemplate 渲染，末尾追加 {tmdb-NNN}</summary>
    /// <remarks>
    /// edition 段双轨：模板含 {edition} 时由占位符就地渲染；模板不含 {edition}（默认）时回退「年份后自动追加 ' - edition'」，
    /// 保证默认模板与改造前 MovieFileBase 逐字节一致（默认模板不含 {edition} token，无 edition 时不加段）。
    /// </remarks>
    public static string MovieFileBase(string title, int year, string? edition, int? tmdbId, NamingTemplateOptions options, string? originalTitle)
    {
        ValidateTitle(title);
        ValidateYear(year);
        bool templateHasEdition = options.MovieNameTemplate.Contains("{edition}", StringComparison.OrdinalIgnoreCase);
        // 模板已带 {edition} → 由占位符渲染；否则把 edition 传 null（占位符渲染用不到）、后面再走「自动追加段」回退
        string body = NamingTemplateRenderer.RenderToken(
            options.MovieNameTemplate,
            MovieTokens(title, year, tmdbId, originalTitle, edition: templateHasEdition ? edition : null));
        if (!templateHasEdition && !string.IsNullOrWhiteSpace(edition))
            body = $"{body} - {Sanitize(edition.Trim())}";
        return $"{body}{TmdbSuffix(tmdbId)}";
    }

    /// <summary>电影占位符字典（值已过 Sanitize；数值为裸数字串，圆括号等修饰来自模板字面量）</summary>
    private static Dictionary<string, string?> MovieTokens(string title, int year, int? tmdbId, string? originalTitle, string? edition)
        => new(StringComparer.Ordinal)
        {
            ["title"] = Sanitize(title),
            ["originaltitle"] = string.IsNullOrWhiteSpace(originalTitle) ? Sanitize(title) : Sanitize(originalTitle.Trim()),
            ["year"] = year.ToString(),
            ["tmdbid"] = tmdbId is int id and > 0 ? id.ToString() : null,
            ["edition"] = string.IsNullOrWhiteSpace(edition) ? null : Sanitize(edition.Trim()),
        };

    /// <summary>电影完整相对路径：{movieFolder}/{movieFileBase}.{ext}（目录与文件名同带 tmdb 标记）</summary>
    public static string MovieFilePath(string title, int year, string extension, string? edition = null, int? tmdbId = null)
    {
        ValidateExtension(extension);
        string folder = MovieFolder(title, year, tmdbId);
        string file = MovieFileBase(title, year, edition, tmdbId);
        return Path.Combine(folder, $"{file}.{NormalizeExtension(extension)}");
    }

    /// <summary>剧集根目录段：「{清洗剧名} ({年份})」，tmdbId 非空追加「 {tmdb-NNN}」</summary>
    public static string TvShowFolder(string title, int year, int? tmdbId = null)
        => TvShowFolder(title, year, tmdbId, NamingTemplateOptions.Default, null);

    /// <summary>剧集根目录段（模板化）：按 options.TvShowFolderTemplate 渲染主体，末尾追加 {tmdb-NNN}</summary>
    public static string TvShowFolder(string title, int year, int? tmdbId, NamingTemplateOptions options, string? originalTitle)
    {
        ValidateTitle(title);
        ValidateYear(year);
        string body = NamingTemplateRenderer.RenderToken(
            options.TvShowFolderTemplate,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["title"] = Sanitize(title),
                ["originaltitle"] = string.IsNullOrWhiteSpace(originalTitle) ? Sanitize(title) : Sanitize(originalTitle.Trim()),
                ["year"] = year.ToString(),
                ["tmdbid"] = tmdbId is int id and > 0 ? id.ToString() : null,
            });
        return $"{body}{TmdbSuffix(tmdbId)}";
    }

    /// <summary>季目录段：「Season {NN}」；季标题非空时为「Season {NN} {季标题}」；S00 = 特别篇（OVA / Specials）</summary>
    /// <remarks>
    /// 季标题（TMDB 季名如「锻刀村篇」/ 文件解析篇章名）仅作人类可读后缀：Plex / Emby / Jellyfin 仍靠
    /// 「Season NN」前缀 + 单集 SxxEyy 识别季集，后缀不影响刮削匹配。季标题经 Sanitize 清洗非法字符；
    /// 为空 / 全空白退化为纯「Season {NN}」（与改造前完全一致，不影响既有单季剧）。
    /// </remarks>
    public static string SeasonFolder(int season, string? seasonTitle = null)
    {
        if (season < 0 || season > 99)
            throw new ArgumentOutOfRangeException(nameof(season), season, "Season 必须在 [0, 99]");
        string baseFolder = $"Season {season:D2}";
        return string.IsNullOrWhiteSpace(seasonTitle) ? baseFolder : $"{baseFolder} {Sanitize(seasonTitle)}";
    }

    /// <summary>归一化季标题：TMDB 季名（过滤无篇章信息的默认季名）优先，回退文件解析篇章名；都无返回 null</summary>
    /// <remarks>
    /// TMDB 默认季名形如「Season 3」/「第 3 季」/「Specials」/「特别篇」，仅是季号别名，作文件夹后缀会得到
    /// 「Season 03 第 3 季」这类冗余，故过滤；转用文件解析出的篇章名（如「锻刀村篇」）。两者皆缺返回 null。
    /// 供归档（ArchiveService）与去向预览（ReviewService）共用，保证预览即真实落点。
    /// </remarks>
    public static string? NormalizeSeasonTitle(string? tmdbSeasonName, string? parsedSeasonTitle)
    {
        if (!string.IsNullOrWhiteSpace(tmdbSeasonName) && !DefaultSeasonNameRegex.IsMatch(tmdbSeasonName.Trim()))
            return tmdbSeasonName.Trim();
        if (!string.IsNullOrWhiteSpace(parsedSeasonTitle))
            return parsedSeasonTitle.Trim();
        return null;
    }

    /// <summary>单集 / 双集合并文件名（不含扩展名）：「{剧名 (年份)} - S{季:D2}E{集:D2}」或「... - S{季:D2}E{起:D2}-E{终:D2}」</summary>
    /// <remarks>
    /// episodeEnd 用于双集 / 多集合并文件（如 PT 站 [第08-09集] / S01E08-E09 命名）。
    /// 不传 episodeEnd 或与 episode 相等 → 单集格式 SxxEyy；
    /// 否则使用 Plex 官方支持的范围格式 SxxEaa-Ebb（参见 Plex 命名指南剧集多集部分）。
    /// </remarks>
    public static string TvEpisodeFileBase(string title, int year, int season, int episode, int? episodeEnd = null)
        => TvEpisodeFileBase(title, year, season, episode, episodeEnd, NamingTemplateOptions.Default, null);

    /// <summary>单集 / 双集合并文件名主体（模板化）：按 options.TvEpisodeNameTemplate 渲染，{episode} 整体锁为 SxxEyy / 范围</summary>
    /// <remarks>
    /// {episode} 占位符整体渲染为 FormatEpisodeToken 产出的 S{季:D2}E{集:D2}（或范围 SxxEaa-Ebb），字母 S/E + 两位补零不可改；
    /// 模板必含 {episode}（由 ValidateTemplate 在写入时保证）。season/episode/episodeEnd 数值越界仍在此抛 ArgumentOutOfRangeException。
    /// 单集文件名按 Plex 约定不带 {tmdb-NNN}（标记仅落剧集根目录）。
    /// </remarks>
    public static string TvEpisodeFileBase(string title, int year, int season, int episode, int? episodeEnd, NamingTemplateOptions options, string? originalTitle)
    {
        ValidateTitle(title);
        ValidateYear(year);
        string episodeToken = FormatEpisodeToken(season, episode, episodeEnd);
        return NamingTemplateRenderer.RenderToken(
            options.TvEpisodeNameTemplate,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["title"] = Sanitize(title),
                ["originaltitle"] = string.IsNullOrWhiteSpace(originalTitle) ? Sanitize(title) : Sanitize(originalTitle.Trim()),
                ["year"] = year.ToString(),
                ["seasonyear"] = year.ToString(),
                ["tmdbid"] = null, // 单集文件名不带 {tmdb-NNN}（标记仅落剧集根）；模板若误填 {tmdbid} 渲染为缺值省略
                ["episode"] = episodeToken,
            });
    }

    /// <summary>集号段格式化：S{季:D2}E{集:D2}，双集（episodeEnd&gt;episode）为 S{季:D2}E{起:D2}-E{终:D2}；季/集越界抛异常</summary>
    /// <remarks>
    /// 集号 S/E 字母 + 两位补零是 Plex / Emby / Jellyfin 识别单集的硬约定，**不进模板、不可改**；
    /// 供 TvEpisodeFileBase 渲染 {episode} 占位符共用，保证字母大小写 + 补零位数全栈一致。
    /// 集号下界含 0：动漫前导集 / 第 0 话（[00].mkv / EP00 / 第0话）是合法集号，生成 SxxE00，与 Season 00（特别篇）同口径。
    /// </remarks>
    public static string FormatEpisodeToken(int season, int episode, int? episodeEnd)
    {
        if (season < 0 || season > 99)
            throw new ArgumentOutOfRangeException(nameof(season), season, "Season 必须在 [0, 99]");
        if (episode < 0 || episode > 9999)
            throw new ArgumentOutOfRangeException(nameof(episode), episode, "Episode 必须在 [0, 9999]");
        if (episodeEnd is int e)
        {
            if (e < 0 || e > 9999)
                throw new ArgumentOutOfRangeException(nameof(episodeEnd), e, "EpisodeEnd 必须在 [0, 9999]");
            if (e < episode)
                throw new ArgumentOutOfRangeException(nameof(episodeEnd), e, "EpisodeEnd 必须 >= Episode");
            if (e > episode)
                return $"S{season:D2}E{episode:D2}-E{e:D2}";
            // e == episode → 退化为单集格式
        }
        return $"S{season:D2}E{episode:D2}";
    }

    /// <summary>单集 / 双集合并完整相对路径：{tvFolder}/{seasonFolder}/{episodeFile}.{ext}（标记仅落在剧集根目录）</summary>
    /// <remarks>
    /// 多季差异化命名：根目录用整剧 year（Plex 靠 {tmdb} + 根目录唯一识别全剧，年份不能按季漂移，否则一部剧裂成多部）；
    /// 季目录可带 seasonTitle；季内单集文件名年份用 seasonYear（该季首播年，缺失回退整剧 year）。
    /// seasonYear / seasonTitle 省略 → 退化为改造前行为（年份全用 year、季目录纯 Season NN）。
    /// </remarks>
    public static string TvEpisodeFilePath(
        string title, int year, int season, int episode, string extension,
        int? episodeEnd = null, int? tmdbId = null, int? seasonYear = null, string? seasonTitle = null)
    {
        ValidateExtension(extension);
        int episodeYear = seasonYear ?? year;
        return Path.Combine(
            TvShowFolder(title, year, tmdbId),
            SeasonFolder(season, seasonTitle),
            $"{TvEpisodeFileBase(title, episodeYear, season, episode, episodeEnd)}.{NormalizeExtension(extension)}");
    }

    /// <summary>电影 .nfo 路径：{movieFolder}/{movieFileBase}.nfo</summary>
    public static string MovieNfoPath(string title, int year, string? edition = null, int? tmdbId = null) =>
        MovieFilePath(title, year, "nfo", edition, tmdbId);

    /// <summary>剧集根 .nfo 路径：{tvFolder}/tvshow.nfo</summary>
    public static string TvShowNfoPath(string title, int year, int? tmdbId = null) =>
        Path.Combine(TvShowFolder(title, year, tmdbId), "tvshow.nfo");

    /// <summary>单集 .nfo 路径</summary>
    public static string TvEpisodeNfoPath(string title, int year, int season, int episode, int? tmdbId = null) =>
        TvEpisodeFilePath(title, year, season, episode, "nfo", tmdbId: tmdbId);

    /// <summary>电影主海报路径：{movieFolder}/poster.jpg</summary>
    public static string MoviePosterPath(string title, int year, int? tmdbId = null) =>
        Path.Combine(MovieFolder(title, year, tmdbId), "poster.jpg");

    /// <summary>电影 fanart 路径：{movieFolder}/fanart.jpg</summary>
    public static string MovieFanartPath(string title, int year, int? tmdbId = null) =>
        Path.Combine(MovieFolder(title, year, tmdbId), "fanart.jpg");

    /// <summary>剧集主海报路径：{tvFolder}/poster.jpg</summary>
    public static string TvShowPosterPath(string title, int year, int? tmdbId = null) =>
        Path.Combine(TvShowFolder(title, year, tmdbId), "poster.jpg");

    /// <summary>剧集 fanart 路径：{tvFolder}/fanart.jpg</summary>
    public static string TvShowFanartPath(string title, int year, int? tmdbId = null) =>
        Path.Combine(TvShowFolder(title, year, tmdbId), "fanart.jpg");

    /// <summary>季海报路径：{tvFolder}/season{NN}-poster.jpg</summary>
    public static string SeasonPosterPath(string title, int year, int season, int? tmdbId = null)
    {
        if (season < 0 || season > 99)
            throw new ArgumentOutOfRangeException(nameof(season), season, "Season 必须在 [0, 99]");
        return Path.Combine(TvShowFolder(title, year, tmdbId), $"season{season:D2}-poster.jpg");
    }

    /// <summary>替换非法字符 + 末尾点/空格清理</summary>
    /// <remarks>
    /// 禁字符集合用 Windows 严格规则：&lt; &gt; : " / \ | ? *（已涵盖 POSIX 必禁 / + NUL）
    /// trim 末尾点 + 空格（Windows 资源管理器拒绝以点或空格结尾的文件名）
    /// 多空格折叠成 1 个，避免清理后出现「a  b」
    /// </remarks>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("标题不能为空", nameof(raw));

        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            // 控制字符（含 NUL）+ Windows 禁字符 → 替换 _
            if (c < 0x20 || c == '<' || c == '>' || c == ':' || c == '"'
                || c == '/' || c == '\\' || c == '|' || c == '?' || c == '*')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        string s = sb.ToString();
        s = MultiSpaceRegex.Replace(s, " ").Trim();
        s = s.TrimEnd('.', ' ');
        if (s.Length == 0)
            throw new ArgumentException("标题清洗后为空（全是非法字符）", nameof(raw));
        // Windows 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9）作为路径段会令文件操作打开设备而非文件；
        // 命中（忽略大小写、忽略扩展名，如 "CON.mkv"）则加下划线前缀规避。
        if (ReservedDeviceNameRegex.IsMatch(s))
            s = "_" + s;
        return s;
    }

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Windows 保留设备名：整段或「设备名.扩展」均触发（CON / PRN / AUX / NUL / COM1-9 / LPT1-9）</summary>
    private static readonly Regex ReservedDeviceNameRegex = new(
        @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>TMDB 默认季名模式（仅季号别名、无篇章信息）：Season N / 第N季 / Specials / 特别篇，用于 NormalizeSeasonTitle 过滤</summary>
    private static readonly Regex DefaultSeasonNameRegex = new(
        @"^(season\s*\d+|第\s*[0-9一二三四五六七八九十百零]+\s*季|specials?|特别篇)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>TmdbId 标记后缀：「 {tmdb-NNN}」（Plex 官方匹配标记）；id 为空或 ≤0 返回空串</summary>
    private static string TmdbSuffix(int? tmdbId) =>
        tmdbId is int id and > 0 ? $" {{tmdb-{id}}}" : string.Empty;

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("标题不能为空", nameof(title));
    }

    private static void ValidateYear(int year)
    {
        if (year < 1888 || year > 2200)
            throw new ArgumentOutOfRangeException(nameof(year), year, "年份必须在 [1888, 2200]");
    }

    private static void ValidateExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            throw new ArgumentException("扩展名不能为空", nameof(ext));
    }

    /// <summary>归一化扩展名：去前导点 + 小写</summary>
    public static string NormalizeExtension(string ext)
    {
        ValidateExtension(ext);
        string e = ext.Trim().TrimStart('.').ToLowerInvariant();
        if (e.Length == 0)
            throw new ArgumentException("扩展名不能仅为点或空白", nameof(ext));
        return e;
    }
}
