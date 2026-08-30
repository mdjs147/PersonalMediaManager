using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Dtos.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>内置规则字典（RuleEngineService 在所有用户规则之后兜底运行的静态正则集合）</summary>
/// <remarks>
/// 唯一数据源：Regex 实例（供 RuleEngineService 使用）+ UI 展示元数据（供 /parse-rules/builtin 端点）。
/// 命中流程 / 置信度评分见 RuleEngineService；本类只负责描述，不参与匹配编排。
///
/// Order 字段是 UI 展示顺序（与 RuleEngineService 内部使用顺序大致一致：季集 → 标题清洗类）；
/// 不代表「优先级」——内置规则之间不是按优先级互相覆盖，而是各自负责不同维度（季 / 集 / 年份 / 噪声）。
///
/// 正则安全：全部 RegexOptions.Compiled + 500ms Timeout，与用户规则保持同等 ReDoS 防护。
/// </remarks>
internal static class BuiltinRulesCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private const RegexOptions BaseOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions NoIgnoreCase = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // ---------- Pattern 字符串（同时供 Regex 编译 + UI 展示） ----------

    public const string SeasonEpisodeLatinPattern =
        // 支持 S01E02 / s5e15 / S01.E02 / S01-E02；双集合并两种形态（episodeEnd 命名组）：
        //   1. 显式范围标记 - 或 ~（可选跟 E/EP）：S01E08-E09 / S01E08-9；
        //   2. 直连无分隔但必须带 E/EP 前缀：S01E08E09 / S01EP08EP09。
        // 直连形态强制 E/EP 前缀、范围外形态强制 -/~ 标记，均为避免把后续 .1080p 等技术参数误识别为范围末端
        @"[Ss]\.?(?<season>\d{1,2})[\.\-_\s]*[Ee][Pp]?(?<episode>\d{1,4})(?:(?:[\-~][Ee]?[Pp]?|[Ee][Pp]?)(?<episodeEnd>\d{1,4}))?";

    public const string SeasonChinesePattern =
        // 同时支持阿拉伯数字「第2季」与中文数字「第二季 / 第十季 / 第二十一季」（1-99）；
        // 中文数字捕获后由 RuleEngineService.ParseCjkSeason 转 int（含「十」「两」）。
        @"第(?<season>[0-9]{1,2}|[一二三四五六七八九十两]{1,3})季";

    public const string EpisodeChinesePattern =
        // 单集「第N集/话/話」+ 双集合并「第08-09集」「第08~09集」（episodeEnd 命名组）
        @"第(?<episode>\d{1,4})(?:[\-~](?<episodeEnd>\d{1,4}))?[集话話]";

    public const string EpisodeOnlyPattern =
        // 单集 EP/E + 数字；可选范围 EP08-E09 / E08-09
        @"(?:^|[\.\-_\s\[])[Ee][Pp]?(?<episode>\d{1,4})(?:[\-~](?:[Ee][Pp]?)?(?<episodeEnd>\d{1,4}))?(?:$|[\.\-_\s\]])";

    public const string BracketEpisodePattern =
        // 单集 [01] + 范围 [08-09] / [08~09]
        @"\[(?<episode>\d{1,4})(?:[\-~](?<episodeEnd>\d{1,4}))?\]";

    /// <summary>压制代号-集号整串形态（DACZLNF-09 / YTYHXBYL-30 一类无标题文件名）</summary>
    /// <remarks>
    /// 下载站单集常以「拼音/英文缩写代号 + 连字符 + 集号」命名整个文件名（剧名只在父目录）：
    /// 字母段是压制组 / 剧名缩写而非可搜索标题——既有规则提不出集号（数字无 E/EP 前缀），
    /// 标题选取又会把字母段误当有效标题（如 YTYHXBYL）→ TMDB 零候选 → 白走一遍 AI。
    /// 整串命中本形态时：数字段作集号兜底（1900-2099 的 4 位年份除外，由使用点守护），
    /// 该层不参与标题竞选（标题回落父目录层）。锚定 ^$ 仅匹配「stem 恰为该形态」，
    /// 正常文件名（Show-2020.1080p 等携带其他内容）不会整串命中。
    /// </remarks>
    public const string ReleaseTagEpisodePattern =
        @"^(?<tag>[A-Za-z]{3,12})[\-_](?<episode>\d{1,4})$";

    /// <summary>独立季号「Sxx」无 E 跟随（如 Born.with.Luck.S01.2026）</summary>
    public const string SeasonOnlyLatinPattern =
        @"(?:^|[\.\-_\s])[Ss](?<season>\d{1,2})(?![\dEe])";

    /// <summary>英文全词季号「Season NN」（标准目录布局 Show (2020)/Season 02/07.mkv 的季目录段）</summary>
    /// <remarks>
    /// 与 SeasonOnlyLatin（紧贴形 S01）互补，主要在「目录段层」生效：多季剧文件名只剩集号时由季目录补季。
    /// 守护：「Season」前不能是字母（防 Preseason / Offseason 误吞）；数字后不能再跟数字
    /// ——「Season 2020」会因 \d{1,2} 所有回溯均撞 (?!\d) 而整体不命中，年份交给 Year 规则，
    /// 同时季号天然限定 1-2 位（与 SxxExx / SeasonOnlyLatin 同口径，不产出归档层无法接受的 3 位季号）。
    /// </remarks>
    public const string SeasonWordLatinPattern =
        @"(?<![A-Za-z])Season[\s\._\-]*(?<season>\d{1,2})(?!\d)";

    /// <summary>罗马数字季号（标题尾部 II-X，主要用于动漫如「刀剑神域II」「进击的巨人 III」）</summary>
    /// <remarks>
    /// 前置非字母数字（避免吃单词内字母 / H264 等），罗马数字按长度降序排列（防 VIII 被 V 提前截断），
    /// 后接「可选分隔符 + 结尾 / 方括号 / 数字（集号）」——要求其落在段尾或紧跟集号，
    /// 避免把标题中段的罗马数字（如 Final Fantasy VII Remake）误当季号。仅 II 及以上（不认单 I）。
    /// 电影续集（教父 II）若被识别为季，下游 TMDB tv 查不到 → AI 纠正为 movie（归档忽略 season），结果仍正确。
    /// 罗马数字 → int 由 RuleEngineService.ParseRomanSeason 转换。
    /// </remarks>
    public const string SeasonRomanPattern =
        @"(?<![A-Za-z0-9])(?<roman>VIII|VII|III|VI|IV|IX|II|V|X)(?=[\s\.\-_]*(?:$|\[|\d))";

    /// <summary>季的篇章标题（中文「XXX篇」，如「锻刀村篇 / 柱训练篇 / 游郭篇 / 无限列车篇」），以篇章名标识季的番剧用</summary>
    /// <remarks>
    /// 要求篇章名前有分隔符或开头、「篇」后非中文，避免把主标题尾字吞进篇章；捕获含「篇」的完整篇章名，
    /// 便于与 TMDB 季名对照。TMDB 季名多为日文（如「刀鍛冶の里編」），自动字符匹配不可靠，主要供人工对照选季。
    /// </remarks>
    public const string SeasonArcPattern =
        @"(?:^|[\s\.\-_])(?<seasonTitle>[一-鿿]{2,8}篇)(?![一-鿿])";

    public const string YearPattern =
        @"(?<![\d])(?<year>(?:19|20)\d{2})(?![\d])";

    public const string NoisePattern =
        @"\b(?:" +
        // 帧率（60fps / 120FPS）须在纯分辨率之前整体剥除，避免数字段残留进标题
        @"480p|720p|1080p|1440p|2160p|4K|8K|UHD|\d{2,3}fps|" +
        @"x264|x265|h\.?264|h\.?265|hevc|avc|av1|vp9|" +
        // 裸 web / dl 兜底：WEB-DL 的「-DL」常被发布组尾缀规则先行剥走，残留裸「WEB」；置于 web-dl 完整形态之后
        @"bluray|bdrip|brrip|web[\-\.]?dl|webrip|hdtv|dvdrip|hdrip|tvrip|hdcam|web|dl|" +
        @"hdr10\+?|hdr|dv|dolby[\s\.\-]?vision|" +
        // 音频：补 DDP / EAC3 / DD+ / TrueHD（含 7.1 数字尾巴变体）
        @"ddp\d?(?:\.\d)?|ddp\+?|eac3|dd\+?\d?(?:\.\d)?|" +
        // 独立声道标记（5.1 / 7.1 / 2.0 / 6.1ch / 8ch）；x.y 后紧跟数字时 \b 不成立，不误伤「Evangelion 1.11」等版本号
        @"[1-9]\.[0-2](?:ch)?|[1-9]ch|" +
        @"ac3|aac|dts(?:[\-\.]hd)?|truehd(?:\s?\d(?:\.\d)?)?|atmos|flac|opus|mp3|" +
        @"chs|cht|chi|eng|jpn|kor|gb|big5|sub|cc|" +
        @"10bit|8bit|" +
        @"remux|repack|proper|extended|directors[\.\s]?cut|unrated|" +
        @"complete|season|ep|episode|" +
        // 流媒体平台标识：IQ(爱奇艺) NF(Netflix) AMZN(Amazon) DSNP(Disney+) ATVP(Apple TV+) HMAX/MAX BILI(B 站) YOUKU MGTV TX(腾讯) HULU PCOK(Peacock) STAN CR(Crunchyroll)
        @"iq|nf|amzn|dsnp|atvp|hmax|max|bili|youku|mgtv|tx|hulu|pcok|stan|cr|" +
        // 中文纯噪声词（下载站常见目录层级 / 修饰词，如「正片」目录、「蓝光原盘」「国语中字」后缀）：
        // 保守仅收录不可能构成真实标题的纯噪声词；+ 量词允许纯噪声连写串（蓝光原盘 / 国语中字 / 双语字幕）整段剥除。
        // .NET \b 视 CJK 为单词字符：与真实词粘连（如「高清剧集网」「全职高手」）时边界不成立、不会误伤标题。
        @"(?:正片|花絮|特典|特辑|合集|高清|蓝光|原盘|国语|粤语|中字|双语|字幕|修复版|收藏版|完结|全集)+" +
        @")\b";

    /// <summary>总季数 / 总集数后缀噪声（剧名层「6季 / 全24集 / 第1-6季」等总量标记，区别于单季「第N季」）</summary>
    /// <remarks>
    /// 三种形态（按正则 alternation 自左向右匹配）：
    ///   1. 全/共 N 季/集/部，含全角/半角括号包裹：全6季 / 共26集 / (全24集) / 全6部；
    ///   2. 区间季 第N-M季（区间即「总季数跨度」，区别于单季「第N季」）：第1-6季；
    ///   3. 裸总数 N 季/集/部（无「第」前缀）：6季 / 26集 / 6部。
    /// 第 3 种用三道负向 lookbehind 守护：
    ///   · (?&lt;!第)         —— 单季「第N季」/ 单集「第N集」的 N 不被当裸总数剥（季/集号要保留给提取）；
    ///   · (?&lt;!\d)         —— 不吞更长数字串的尾段（如 12345集 只在串首尝试，匹配不到即整体放过）；
    ///   · (?&lt;!第\d{1,2}[-~]) —— 中文双集「第08-09集」的「09集」不被当裸总数剥（变长 lookbehind，.NET 支持）。
    /// </remarks>
    public const string TotalCountNoisePattern =
        @"(?:" +
        @"[（(\[【]?[全共]\d{1,3}[季集部][）)\]】]?" +
        @"|第\d{1,2}[\-~]\d{1,2}季" +
        @"|(?<!第)(?<!\d)(?<!第\d{1,2}[\-~])\d{1,3}[季集部]" +
        @")";

    /// <summary>方括号块剥离：同时匹配半角 [ ] 和中文全角【 】（PT 站发布组前缀常用 【高清剧集网...】）</summary>
    public const string GroupBracketPattern = @"[\[【][^\[\]【】]{1,60}[\]】]";

    /// <summary>发布组尾缀剥离：'-' 后到结尾或下一个分隔符前的 ASCII 单词（如 -ColorWEB / -FRDS / -CMCT）</summary>
    public const string ReleaseGroupSuffixPattern = @"-[A-Za-z][A-Za-z0-9_]{1,20}(?=$|[\.\s])";

    /// <summary>分隔符折叠：补 + 号（PT 站元信息常用 [国语音轨+简繁英字幕]）</summary>
    public const string SeparatorPattern = @"[\.\-_\+]+";

    // ---------- 预编译 Regex（供 RuleEngineService 引用） ----------

    public static readonly Regex SeasonEpisodeLatin = new(SeasonEpisodeLatinPattern, BaseOptions, Timeout);
    public static readonly Regex SeasonChinese = new(SeasonChinesePattern, NoIgnoreCase, Timeout);
    public static readonly Regex EpisodeChinese = new(EpisodeChinesePattern, NoIgnoreCase, Timeout);
    public static readonly Regex EpisodeOnly = new(EpisodeOnlyPattern, BaseOptions, Timeout);
    public static readonly Regex BracketEpisode = new(BracketEpisodePattern, NoIgnoreCase, Timeout);
    public static readonly Regex ReleaseTagEpisode = new(ReleaseTagEpisodePattern, BaseOptions, Timeout);
    public static readonly Regex SeasonOnlyLatin = new(SeasonOnlyLatinPattern, BaseOptions, Timeout);
    public static readonly Regex SeasonWordLatin = new(SeasonWordLatinPattern, BaseOptions, Timeout);
    // NoIgnoreCase：罗马数字季号一律大写匹配，避免小写编码 token（x264 的 x → X、hevc 的 v → V）被误当季号
    public static readonly Regex SeasonRoman = new(SeasonRomanPattern, NoIgnoreCase, Timeout);
    public static readonly Regex SeasonArc = new(SeasonArcPattern, NoIgnoreCase, Timeout);
    public static readonly Regex Year = new(YearPattern, BaseOptions, Timeout);
    public static readonly Regex Noise = new(NoisePattern, BaseOptions, Timeout);
    public static readonly Regex TotalCountNoise = new(TotalCountNoisePattern, NoIgnoreCase, Timeout);
    public static readonly Regex GroupBracket = new(GroupBracketPattern, NoIgnoreCase, Timeout);
    public static readonly Regex ReleaseGroupSuffix = new(ReleaseGroupSuffixPattern, BaseOptions, Timeout);
    public static readonly Regex Separator = new(SeparatorPattern, RegexOptions.Compiled, Timeout);

    // ---------- UI 展示元数据（GET /parse-rules/builtin 直接返回） ----------

    public static readonly IReadOnlyList<BuiltinParseRuleResponse> All = new BuiltinParseRuleResponse[]
    {
        new(
            Key: "SeasonEpisodeLatin",
            Name: "季集 SxxExx 拉丁格式（含双集合并）",
            Description: "识别 Plex / 美剧站标准命名：S01E02 / s5e15 / SE01EP02；同时支持双集合并 S01E08-E09 / S01E08-9 / S01E08E09，捕获 episode + episodeEnd。",
            Pattern: SeasonEpisodeLatinPattern,
            Order: 10,
            Samples: new[] { "BreakingBad.S01E02.1080p.mkv", "Some.Show.s5e15.HDTV.mkv", "Born.with.Luck.S01E08-E09.mkv", "Born.with.Luck.S01E08E09.mkv" }),

        new(
            Key: "SeasonChinese",
            Name: "中文季号「第N季」",
            Description: "识别 1-99 范围的中文季号：阿拉伯数字「第2季」与中文数字「第二季 / 第十季 / 第二十一季」均支持，用于国产剧 / 华语剧 / 番剧目录名。",
            Pattern: SeasonChinesePattern,
            Order: 20,
            Samples: new[] { "琅琊榜 第1季", "庆余年 第二季", "鬼灭之刃 第三季" }),

        new(
            Key: "EpisodeChinese",
            Name: "中文集号「第N集 / 第N话 / 第N話」（含范围）",
            Description: "识别中文集号标记，兼容简中「集」、繁中「話」、动漫「话」；支持双集范围「第08-09集」「第08~09集」捕获 episode + episodeEnd。",
            Pattern: EpisodeChinesePattern,
            Order: 30,
            Samples: new[] { "绝命毒师 第03集 1080p.mkv", "进击的巨人 第12话.mkv", "低智商犯罪 第08-09集.mkv" }),

        new(
            Key: "EpisodeOnly",
            Name: "集号兜底「EP / E + 数字」（含范围）",
            Description: "无季号、仅集号的兜底匹配；要求 EP/E 前后至少一个分隔符或边界，避免吞掉单词中的字母组合。支持双集 EP08-E09 / E08-09 捕获 episodeEnd。",
            Pattern: EpisodeOnlyPattern,
            Order: 40,
            Samples: new[] { "Show.EP01.1080p.mkv", "Some.Series.E12.mkv", "Show.EP08-E09.mkv" }),

        new(
            Key: "BracketEpisode",
            Name: "方括号集号「[01]」（含范围）",
            Description: "纯数字方括号集号，常见于动漫字幕组、番剧整理目录；支持范围 [08-09] / [08~09] 捕获 episodeEnd。",
            Pattern: BracketEpisodePattern,
            Order: 50,
            Samples: new[] { "[字幕组] 某番 [01][1080p].mkv", "[番名][24].mkv", "[番名][08-09].mkv" }),

        new(
            Key: "ReleaseTagEpisode",
            Name: "压制代号-集号整串兜底（XXXX-09）",
            Description: "文件名整串仅为「字母代号-集号」（如 DACZLNF-09 / YTYHXBYL-30，剧名只在父目录）时：数字段作集号（1900-2099 的 4 位年份除外），字母段视为压制组 / 缩写代号不参与标题选取，标题改从父目录层提取——避免缩写被当标题搜 TMDB 零候选后白走 AI。",
            Pattern: ReleaseTagEpisodePattern,
            Order: 52,
            Samples: new[] { "DACZLNF-09.mkv", "YTYHXBYL-30.mkv" }),

        new(
            Key: "SeasonOnlyLatin",
            Name: "独立季号 Sxx（无 E 跟随）",
            Description: "识别仅有季号、不带集号的标记（如 PT 站父目录 Born.with.Luck.S01.2026）。要求 Sxx 后不紧跟数字或 E，避免与 SxxExx 重复匹配。",
            Pattern: SeasonOnlyLatinPattern,
            Order: 55,
            Samples: new[] { "Born.with.Luck.S01.2026.2160p", "Some.Show.S05.Complete.WEB-DL" }),

        new(
            Key: "SeasonRoman",
            Name: "罗马数字季号（标题尾部 II-X）",
            Description: "识别动漫常见的尾部罗马数字季号（II / III / … / X），如「刀剑神域II」「进击的巨人 III」。仅 II 及以上、大写、位于段尾或紧跟集号，避免误吃编码 token（x264）与标题中段罗马数字；电影续集（教父 II）若误判为季会经 TMDB/AI 纠正回 movie。",
            Pattern: SeasonRomanPattern,
            Order: 56,
            Samples: new[] { "刀剑神域 II [01].mkv", "进击的巨人 III - 12.mkv", "Sword Art Online II - 01.mkv" }),

        new(
            Key: "SeasonArc",
            Name: "篇章季标题「XXX篇」",
            Description: "识别以篇章名标识季的番剧（如「鬼灭之刃 锻刀村篇 / 柱训练篇 / 游郭篇」）。提取篇章名为季标题（SeasonTitle）并从主标题剥离，供审核页人工对照 TMDB 季名选季——TMDB 季名多为日文，自动字符匹配不可靠，故以保留 + 展示为主。",
            Pattern: SeasonArcPattern,
            Order: 57,
            Samples: new[] { "鬼灭之刃 锻刀村篇 第01集.mkv", "鬼灭之刃.柱训练篇.01.mkv" }),

        new(
            Key: "SeasonWordLatin",
            Name: "英文全词季号「Season NN」",
            Description: "识别标准目录布局的季目录段（Show (2020)/Season 02/07.mkv）：多季剧文件名只剩集号时由「Season NN」目录补季号。「Season」前不能是字母（防 Preseason 误吞），数字后不能再跟数字（防「Season 2020」把年份截成季号），季号限 1-2 位。",
            Pattern: SeasonWordLatinPattern,
            Order: 58,
            Samples: new[] { "Show Name (2020)/Season 02/07.mkv", "Season.03/EP05.mkv", "Breaking Bad/season 1/S01E01.mkv" }),

        new(
            Key: "Year",
            Name: "年份提取（1900-2099）",
            Description: "抓取 1900-2099 范围年份；前后必须是非数字，避开误吞 8 位日期 / 长数字的局部。",
            Pattern: YearPattern,
            Order: 60,
            Samples: new[] { "Inception.2010.1080p.mkv", "Movie Title (2020) 720p.mkv", "Show.S01E01.2008.HDTV.mkv" }),

        new(
            Key: "Noise",
            Name: "噪声 token 清洗（含流媒体平台 + DDP/EAC3）",
            Description: "清除分辨率（1080p / 4K / UHD）、帧率（60fps）、编码（x264 / HEVC / AV1）、来源（BluRay / WEB-DL / HDTV，含被尾缀规则拆剩的裸 WEB / DL）、HDR（HDR10 / DV / Dolby Vision）、音频（AC3 / AAC / DTS / DDP / EAC3 / DD+ / TrueHD / Atmos / FLAC）、独立声道（5.1 / 7.1 / 2.0 / 6.1ch）、语种（CHS / CHT / ENG）、流媒体平台（IQ / NF / AMZN / DSNP / ATVP / HMAX / BILI / MGTV / TX / HULU）、压制标记（REMUX / REPACK / EXTENDED）、中文纯噪声词（正片 / 花絮 / 蓝光原盘 / 国语中字 / 双语字幕 / 全集等，仅独立成段时剥除）等 80+ 常见 token，保持标题干净。",
            Pattern: NoisePattern,
            Order: 70,
            Samples: new[] { "Inception.2010.2160p.UHD.BluRay.HEVC.HDR10.Atmos-GROUP.mkv", "Born.with.Luck.S01.2160p.IQ.WEB-DL.H265.DDP5.1-ColorWEB.mkv" }),

        new(
            Key: "TotalCountNoise",
            Name: "总季数 / 总集数后缀清洗（6季 / 全24集 / 第1-6季）",
            Description: "剥离剧名层的「总量」后缀：不带「第」的裸总数（6季 / 26集 / 6部）、「全N季 / 共N集 / 全N部」（含全角括号包裹「(全24集)」）、以及区间季「第1-6季」。保留单季季号「第N季」与单集「第N集」交给季/集号提取，不误伤。",
            Pattern: TotalCountNoisePattern,
            Order: 75,
            Samples: new[] { "国务卿女士 6季", "扫毒 全24集", "纪录片 (全24集)", "某剧 第1-6季", "复仇者 6部" }),

        new(
            Key: "GroupBracket",
            Name: "方括号 / 全角【】块剥离",
            Description: "剥离最多 60 字符的方括号块（半角 [] 与中文全角【】），覆盖字幕组前缀、压制组、PT 站发布组水印（如【高清剧集网发布 www.PTHDTV.com】）。",
            Pattern: GroupBracketPattern,
            Order: 80,
            Samples: new[] { "[ReleaseGroup] My Movie 2020 1080p.mkv", "【高清剧集网发布 www.PTHDTV.com】低智商犯罪.mkv" }),

        new(
            Key: "ReleaseGroupSuffix",
            Name: "发布组尾缀「-Group」剥离",
            Description: "剥离文件名 / 目录名末尾的「-发布组名」（ASCII 字母数字，长度 ≤ 20），如 -ColorWEB / -FRDS / -CMCT / -MeM。仅匹配末尾或紧跟分隔符的位置，避免误吞中间的 -。",
            Pattern: ReleaseGroupSuffixPattern,
            Order: 85,
            Samples: new[] { "Born.with.Luck.S01.WEB-DL-ColorWEB", "Show.S01.1080p-FRDS.mkv" }),

        new(
            Key: "Separator",
            Name: "分隔符折叠（含 +）",
            Description: "把连续的 . / _ / - / + 折叠为单个空格，将「Some.Movie.Title」「[国语音轨+简繁英字幕]」式命名归一化为可读标题。",
            Pattern: SeparatorPattern,
            Order: 90,
            Samples: new[] { "Some.Movie.Title", "Show_Name_S01E01", "国语音轨+简繁英字幕" }),
    };
}
