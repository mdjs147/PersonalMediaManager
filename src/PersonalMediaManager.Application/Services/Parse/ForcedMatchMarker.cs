using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>强制匹配标识 —— 用户在文件夹放置的 pmm.txt / TMDB URL，或文件夹名 {tmdb-NNN} 标记解析后的结构化结果</summary>
/// <remarks>
/// 命中后直接锚定指定 TMDB 条目，跳过规则置信度判定、特殊字符转 AI、TMDB 搜索、AI 升级链全部分支。
/// 两类载体语义不同：
///   · pmm.txt / TMDB URL —— 由网址 /tv|movie/{id} 自带类型，MediaType 非空（锁定类型 + 可选季 / 剧集组）。
///   · 文件夹名 {tmdb-NNN} 标记 —— 只锚定 TMDB id，MediaType 为 null：类型（剧 / 影）、季、集仍由规则引擎正常识别。
/// EpisodeGroupId 非空（剧集组模式）时，还把文件名解析出的"编组内集号"翻译成正典 season/episode
/// （HD Remaster 等重制版场景）。各可选字段缺省即不启用对应覆盖。
/// </remarks>
public sealed record ForcedMatchMarker(
    int TmdbId,
    string? MediaType,
    int? Season,
    string? EpisodeGroupId,
    string? GroupId,
    string? TitleOverride);

/// <summary>pmm.txt / TMDB URL → ForcedMatchMarker 的纯解析（无 IO，便于单测）</summary>
/// <remarks>
/// 容错策略：忽略空行与注释（# / // / ;）；同时认三种写法（可混写，显式 key=value 覆盖 URL 解析）：
///   ① 整行 TMDB URL（含 .url 快捷方式的 URL=... 行）—— 从路径段拆出 tmdb id / 类型 / 季 / 剧集组 / 分组；
///   ② key = value —— tmdb / type / season / episode_group / group / title；
///   ③ 裸数字一行 —— 当作 tmdb id。
/// 必要条件：解析出 TmdbId &gt; 0，否则视为无效标识返回 null。类型缺省为 tv（剧集组本就只剧集有）。
/// </remarks>
public static class ForcedMatchMarkerParser
{
    private static readonly Regex IdRegex = new(@"/(tv|movie)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeasonRegex = new(@"/season/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodeGroupRegex = new(@"/episode_group/([0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GroupRegex = new(@"/group/([0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>TMDB 标记：{tmdb-NNN} / [tmdbid-NNN]（Plex 官方匹配标记，归档落点同款），花括号或方括号、大小写不敏感</summary>
    private static readonly Regex TmdbMarkerRegex = new(@"[\{\[]\s*tmdb(?:id)?\s*-\s*(\d+)\s*[\}\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从文件夹名或文件名解析 {tmdb-NNN} 标记 → 仅锚定 TMDB id 的标识；无标记 / id 无效返回 null</summary>
    /// <remarks>
    /// 接受任意路径段名字 —— 文件夹名，或含扩展名的文件名（标记常在扩展名之前，扩展名不影响匹配）。
    /// 只取 id，不锁类型 / 季：MediaType 返回 null，下游 ProcessFileService 用规则引擎识别出的类型（剧 / 影）锚定，
    /// 季 / 集同样取规则解析值。识别的标记形态与归档侧 PlexNamingConventions 生成的 {tmdb-NNN} 完全一致
    /// （故已归档作品整目录 / 单文件回投会被自动识别、零 TMDB 搜索）；兼容 [tmdb-]/[tmdbid-] 与大写写法、容标记内空白。
    /// 同一名字出现多个标记时取**第一个**（确定性，避免随机命中）。
    /// </remarks>
    public static ForcedMatchMarker? ParseTmdbMarker(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        Match m = TmdbMarkerRegex.Match(name);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int id) || id <= 0) return null;
        return new ForcedMatchMarker(id, MediaType: null, Season: null, EpisodeGroupId: null, GroupId: null, TitleOverride: null);
    }

    /// <summary>解析标识文件全文；无法解析出有效 TmdbId 时返回 null</summary>
    public static ForcedMatchMarker? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        int? tmdbId = null;
        string? type = null;
        int? season = null;
        string? episodeGroup = null;
        string? group = null;
        string? title = null;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith(';'))
                continue;

            // ① TMDB URL 行（裸 URL 或 .url 的 URL=... 行都含 themoviedb.org）
            if (line.Contains("themoviedb.org", StringComparison.OrdinalIgnoreCase))
            {
                ApplyUrl(line, ref tmdbId, ref type, ref season, ref episodeGroup, ref group);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                // ③ 裸数字一行 → tmdb id
                if (int.TryParse(line, out int bare) && bare > 0) tmdbId = bare;
                continue;
            }

            // ② key = value
            string key = line[..eq].Trim().ToLowerInvariant();
            string val = line[(eq + 1)..].Trim();
            if (val.Length == 0) continue;
            switch (key)
            {
                case "tmdb" or "tmdbid" or "id":
                    if (int.TryParse(val, out int t) && t > 0) tmdbId = t;
                    break;
                case "type" or "mediatype":
                    type = NormalizeType(val) ?? type;
                    break;
                case "season" or "s":
                    if (int.TryParse(val, out int sn) && sn >= 0) season = sn;
                    break;
                case "episode_group" or "episodegroup" or "eg":
                    episodeGroup = val;
                    break;
                case "group" or "groupid":
                    group = val;
                    break;
                case "title" or "name":
                    title = val;
                    break;
            }
        }

        if (tmdbId is not int id || id <= 0) return null;
        return new ForcedMatchMarker(
            id,
            type ?? "tv",
            season,
            Blank2Null(episodeGroup),
            Blank2Null(group),
            Blank2Null(title));
    }

    private static void ApplyUrl(string url, ref int? tmdbId, ref string? type, ref int? season, ref string? eg, ref string? group)
    {
        Match m = IdRegex.Match(url);
        if (m.Success)
        {
            type = m.Groups[1].Value.ToLowerInvariant();
            if (int.TryParse(m.Groups[2].Value, out int id) && id > 0) tmdbId = id;
        }
        Match sm = SeasonRegex.Match(url);
        if (sm.Success && int.TryParse(sm.Groups[1].Value, out int s)) season = s;
        Match em = EpisodeGroupRegex.Match(url);
        if (em.Success) eg = em.Groups[1].Value;
        Match gm = GroupRegex.Match(url);
        if (gm.Success) group = gm.Groups[1].Value;
    }

    private static string? NormalizeType(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "tv" or "series" or "show" or "剧集" or "电视剧" or "动漫" or "番剧" => "tv",
        "movie" or "film" or "电影" => "movie",
        _ => null,
    };

    private static string? Blank2Null(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
