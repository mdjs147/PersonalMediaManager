using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Common.Archiving;

/// <summary>归档命名模板渲染 + 校验（电影目录/文件、剧集根目录、单集文件名三类主体）</summary>
/// <remarks>
/// 纯逻辑、无 IO、无 DB（留 Application 层）。渲染只产「{tmdb-NNN} 后缀之前的主体」，
/// {tmdb-NNN} / Season NN / SxxEyy / 伴随文件名等刮削必需结构由调用方（PlexNamingConventions）锁死。
///
/// 渲染契约（保证 Default 模板逐字节 == 改造前硬编码）：
///   · 扫描模板里的 {name} 占位符，用 tokens 字典对应值替换（值由调用方预先过 Sanitize）；
///   · {episode} 整体替换为 PlexNamingConventions.FormatEpisodeToken 产出的 SxxEyy / 范围串（字母 S/E + 两位补零锁死）；
///   · {year} / {tmdbid} / {seasonyear} 为裸数字（圆括号等修饰由模板字面量自带，如默认「({year})」）；
///   · 未提供值的占位符（如电影无 {edition}）按缺值规则整段省略其相邻修饰（见 RenderToken 注释）；
///   · 渲染后整体折叠多空格 + trim 末尾点/空格，空结果抛 ArgumentException（与 Sanitize 失败口径一致）。
///
/// 校验契约（ValidateTemplate，写入设置 + 预览端点共用）：
///   · 拒绝 {tmdb 字面量（防与代码追加的 {tmdb-NNN} 后缀重复 / 破坏锚点匹配）；
///   · 拒绝路径分隔符 / 与 \（模板只渲染单个目录/文件名段，不开放层级编排）；
///   · 拒绝未知占位符（只认本类白名单）；
///   · 剧集单集模板必须含 {episode}（否则集号丢失，Plex 无法识别单集）。
/// </remarks>
public static class NamingTemplateRenderer
{
    /// <summary>模板种类：决定占位符白名单与强制规则</summary>
    public enum TemplateKind
    {
        /// <summary>电影目录名 + 电影文件名主体（同一模板，允许 {edition}）</summary>
        Movie,

        /// <summary>剧集根目录主体（不允许 {edition} / {episode} / {seasonyear}）</summary>
        TvShowFolder,

        /// <summary>单集文件名主体（强制含 {episode}，允许 {seasonyear}）</summary>
        TvEpisode,
    }

    /// <summary>各模板种类允许的占位符名（小写，不含花括号）</summary>
    private static readonly IReadOnlyDictionary<TemplateKind, IReadOnlySet<string>> AllowedTokens =
        new Dictionary<TemplateKind, IReadOnlySet<string>>
        {
            [TemplateKind.Movie] = new HashSet<string>(StringComparer.Ordinal) { "title", "originaltitle", "year", "tmdbid", "edition" },
            [TemplateKind.TvShowFolder] = new HashSet<string>(StringComparer.Ordinal) { "title", "originaltitle", "year", "tmdbid" },
            [TemplateKind.TvEpisode] = new HashSet<string>(StringComparer.Ordinal) { "title", "originaltitle", "year", "tmdbid", "episode", "seasonyear" },
        };

    /// <summary>占位符词法：{ + 字母 + }（大小写不敏感由调用统一 ToLower 处理；此处只抓形如 {abc} 的段）</summary>
    private static readonly Regex TokenRegex = new(@"\{([A-Za-z]+)\}", RegexOptions.Compiled);

    /// <summary>多空格折叠（与 PlexNamingConventions 口径一致）</summary>
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>模板校验结果：IsValid + 人类可读错误清单（中文）</summary>
    public sealed record ValidationOutcome(bool IsValid, IReadOnlyList<string> Errors)
    {
        public static readonly ValidationOutcome Ok = new(true, Array.Empty<string>());
    }

    /// <summary>校验单个模板是否合法（拒绝 {tmdb / 路径分隔符 / 未知占位符；剧集单集必须含 {episode}）</summary>
    public static ValidationOutcome ValidateTemplate(string? template, TemplateKind kind)
    {
        List<string> errors = new();
        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add("模板不能为空");
            return new ValidationOutcome(false, errors);
        }

        // {tmdb-NNN} 锚点由代码在 Sanitize 之后强制追加；模板含 {tmdb 字面量会重复 / 破坏锚点匹配
        if (template.Contains("{tmdb", StringComparison.OrdinalIgnoreCase))
            errors.Add("模板不能包含 {tmdb 字面量：{tmdb-NNN} 匹配标记由系统自动追加，请勿手写");

        // 模板只渲染单个目录/文件名段，禁止路径分隔符（层级编排由系统按 Plex 结构固定）
        if (template.Contains('/') || template.Contains('\\'))
            errors.Add("模板不能包含路径分隔符 / 或 \\（目录层级由系统按 Plex 结构固定）");

        IReadOnlySet<string> allowed = AllowedTokens[kind];
        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (Match m in TokenRegex.Matches(template))
        {
            string name = m.Groups[1].Value.ToLowerInvariant();
            present.Add(name);
            if (!allowed.Contains(name))
                errors.Add($"未知或不适用于本模板的占位符：{{{m.Groups[1].Value}}}（可用：{string.Join(" / ", allowed.Select(a => "{" + a + "}"))}）");
        }

        // 剧集单集模板必须含 {episode}（否则集号丢失，Plex 无法识别单集）
        if (kind == TemplateKind.TvEpisode && !present.Contains("episode"))
            errors.Add("单集文件名模板必须包含 {episode}（即 SxxEyy 集号，刮削识别单集所必需）");

        return errors.Count == 0 ? ValidationOutcome.Ok : new ValidationOutcome(false, errors);
    }

    /// <summary>渲染模板主体：用 tokens 替换 {name}（值须已过 Sanitize），缺值占位符整段省略；trim 后空则抛异常</summary>
    /// <remarks>
    /// 缺值规则（保证默认模板缺 edition 时逐字节 == 改造前「无 edition 不加段」）：
    ///   tokens 里值为 null 的占位符视为「缺值」，连同其紧邻的「左侧分隔修饰」一并省略——
    ///   具体做法：先把 {token} 替换为占位标记，最后用正则把「空标记及其前导 ' - ' / 多余空格」收敛掉。
    ///   这样 "{title} ({year}) - {edition}" 在无 edition 时得到 "{title} ({year})"（与 MovieFileBase 一致）。
    /// {year} / {tmdbid} / {seasonyear} 等数值占位符由调用方以裸数字字符串传入（不含括号），圆括号等来自模板字面量。
    /// </remarks>
    public static string RenderToken(string template, IReadOnlyDictionary<string, string?> tokens)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new ArgumentException("模板不能为空", nameof(template));

        // 用一个不可能出现在正常文案里的哨兵标记「缺值占位符」，便于后续把「' - ' + 空值」整体收敛
        const string MissingSentinel = ""; // Unicode 私有区，渲染文案绝不含此字符

        string rendered = TokenRegex.Replace(template, m =>
        {
            string name = m.Groups[1].Value.ToLowerInvariant();
            if (!tokens.TryGetValue(name, out string? value))
                return m.Value; // 未在字典中的占位符（理论上已被 ValidateTemplate 拦截）原样保留
            return string.IsNullOrEmpty(value) ? MissingSentinel : value;
        });

        // 收敛缺值：把「可选分隔段（前导空格 + '-' + 空格）+ 哨兵」整体抹掉（处理 " - {edition}" 缺 edition 的情形）；
        // 再把残留的孤立哨兵抹掉（处理模板开头/无分隔的缺值占位符）。
        rendered = Regex.Replace(rendered, @"\s*-\s*" + MissingSentinel, string.Empty);
        rendered = rendered.Replace(MissingSentinel, string.Empty);

        // 折叠多空格 + trim 末尾点/空格（Windows 文件名不允许以点或空格结尾），与 Sanitize 口径一致
        rendered = MultiSpaceRegex.Replace(rendered, " ").Trim();
        rendered = rendered.TrimEnd('.', ' ');
        if (rendered.Length == 0)
            throw new ArgumentException("模板渲染后为空（所有占位符均缺值且无字面量）", nameof(template));
        return rendered;
    }
}
