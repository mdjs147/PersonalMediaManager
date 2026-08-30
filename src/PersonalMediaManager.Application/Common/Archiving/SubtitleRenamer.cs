using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Common.Archiving;

/// <summary>字幕重命名规则（D4.3，需求文档 §3.6.3）</summary>
/// <remarks>
/// 输入：发现到的字幕文件清单（同源视频的同目录文件 + Subs 等字幕子目录文件），由调用方按视频文件分组传入。
/// 输出：每个字幕的「目标 Plex 标准名 + 处理动作（Rename / Skip）」决策列表，不做真实 IO。
/// 真实 mv 由 IFileMover 在 ArchiveService（D7.4）执行；归属判定（stem 前缀 / 季集匹配 / 电影收编）
/// 的纯函数部分（DetectSeasonEpisode / MatchesEpisode）也内聚于本类，便于单测。
///
/// 规则要点：
/// - 语言识别：基于文件名子串匹配（大小写不敏感），支持 CHS/SC/简/Chinese / CHT/TC/繁/Traditional / ENG/EN/English / JPN/JP/Japanese；
///   文件名识别不出时回退用 PathHint 目录段（从深到浅）识别——覆盖「Subs/CHS/xxx.srt」按语言分目录的发布结构
/// - 未识别 → "und"（undetermined）
/// - 同语言多文件：按文件大小排序，**最大**的不加序号（如 "{base}.zh.srt"），其余按从小到大（与最大同向逆序）追加 .2 .3 .N
///   原因：制作精良的字幕通常字节数更大（含完整时间码 + 注释 + 特效）
/// - 字幕压缩包 .zip / .rar / .7z → Skip（不引入解压依赖，避免安全风险）
/// - 内嵌字幕（MKV 内置轨）→ 调用方不传给本类
///
/// 决策不涉及 IO：方便 mock + 在 ArchiveService 内串行执行 IFileMover.MoveAsync。
/// </remarks>
public static class SubtitleRenamer
{
    /// <summary>支持的字幕扩展名（小写带点）</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".smi",
    };

    /// <summary>被识别为压缩包 → 跳过的扩展名（不解压，避免安全风险）</summary>
    public static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz",
    };

    /// <summary>对一组同源字幕文件计算重命名决策</summary>
    /// <param name="targetFileBase">目标视频文件名（不含扩展名）— 字幕重命名以此为前缀</param>
    /// <param name="subtitles">字幕候选清单（同目录扫到的所有字幕样品）</param>
    public static IReadOnlyList<SubtitleRenameDecision> Plan(
        string targetFileBase,
        IReadOnlyList<SubtitleCandidate> subtitles)
    {
        if (string.IsNullOrWhiteSpace(targetFileBase))
            throw new ArgumentException("目标文件基名不能为空", nameof(targetFileBase));
        if (subtitles is null || subtitles.Count == 0) return [];

        List<SubtitleRenameDecision> decisions = new(subtitles.Count);
        List<(SubtitleCandidate Candidate, string Lang)> renameable = new(subtitles.Count);

        // Phase 1：分流（压缩包跳过，其余按语言归类）
        foreach (SubtitleCandidate sub in subtitles)
        {
            string ext = Path.GetExtension(sub.FileName);
            if (ArchiveExtensions.Contains(ext))
            {
                decisions.Add(new SubtitleRenameDecision(sub, TargetFileName: null, Action: SubtitleAction.SkipArchive,
                    Reason: "字幕压缩包不解压（避免引入解压依赖与安全风险）"));
                continue;
            }
            if (!SupportedExtensions.Contains(ext))
            {
                decisions.Add(new SubtitleRenameDecision(sub, TargetFileName: null, Action: SubtitleAction.SkipUnsupported,
                    Reason: $"不支持的字幕扩展名：{ext}"));
                continue;
            }
            string lang = DetectLanguage(sub.FileName);
            if (lang == "und" && !string.IsNullOrEmpty(sub.PathHint))
            {
                // 文件名识别不出语言时，回退用子目录段从深到浅识别（「Subs/CHS/movie.srt」按语言分目录）
                string[] segments = sub.PathHint.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = segments.Length - 1; i >= 0 && lang == "und"; i--)
                    lang = DetectLanguage(segments[i]);
            }
            renameable.Add((sub, lang));
        }

        // Phase 2：同语言多文件：按大小降序，最大不加序号，其余追加 .2 / .3 ...
        IEnumerable<IGrouping<string, (SubtitleCandidate Candidate, string Lang)>> byLang =
            renameable.GroupBy(x => x.Lang, StringComparer.Ordinal);

        foreach (IGrouping<string, (SubtitleCandidate Candidate, string Lang)> grp in byLang)
        {
            (SubtitleCandidate Candidate, string Lang)[] sorted = grp
                .OrderByDescending(x => x.Candidate.SizeBytes)
                .ThenBy(x => x.Candidate.FileName, StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < sorted.Length; i++)
            {
                SubtitleCandidate sub = sorted[i].Candidate;
                string lang = sorted[i].Lang;
                string ext = Path.GetExtension(sub.FileName);
                string ordinalSuffix = i == 0 ? string.Empty : $".{i + 1}";
                string target = $"{targetFileBase}{ordinalSuffix}.{lang}{ext.ToLowerInvariant()}";
                decisions.Add(new SubtitleRenameDecision(sub, target, SubtitleAction.Rename, Reason: null));
            }
        }

        return decisions;
    }

    /// <summary>从文件名子串推断语言代码</summary>
    public static string DetectLanguage(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        // 大小写不敏感匹配；按更具体（CHT/TC/繁）优先于通用（zh）
        foreach ((string token, string code) in LanguageTokens)
        {
            if (Regex.IsMatch(baseName, $@"(?<![A-Za-z0-9]){Regex.Escape(token)}(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return code;
        }
        return "und";
    }

    // ---------- 季集标记检测（字幕与视频不同名时的归属判定，需求文档 §3.6.3 启发式匹配） ----------

    /// <summary>从单段文本（文件名 stem 或目录段）提取季集标记</summary>
    /// <remarks>
    /// 模式（按强度排序，强模式命中即定）：
    /// - SxxEyy / SxxEPyy / Sxx.Eyy（季+集，一锤定音）
    /// - 仅集：第N集/话/話 → EP01/E01/EP.01 → Episode N → [NN] → 整段纯数字（目录段「01」）
    /// - 仅季：第N季 → Season N → 独立 Sxx 词
    /// 防误抓：[NN] 与纯数字段排除分辨率（480/576/720/1080/2160）与年份（1900-2099）——
    /// 集数恰为这些值的极端场景（如 [2024] 是集数）按漏匹配处理，字幕通常另带 EP/SxxEyy 强标记可兜住。
    /// 返回 (Season, Episode) 各自可空；全空 = 该段无季集标记。
    /// </remarks>
    public static (int? Season, int? Episode) DetectSeasonEpisode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        Match m = SeasonEpisodePattern.Match(text);
        if (m.Success)
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));

        int? season = null;
        foreach (Regex r in SeasonOnlyPatterns)
        {
            Match sm = r.Match(text);
            if (sm.Success) { season = int.Parse(sm.Groups[1].Value); break; }
        }

        int? episode = null;
        foreach (Regex r in EpisodeOnlyPatterns)
        {
            Match em = r.Match(text);
            if (!em.Success) continue;
            int value = int.Parse(em.Groups[1].Value);
            // 方括号 / 纯数字弱模式须排除分辨率与年份；强模式（第N集 / EP01）不受限
            if (r == BracketEpisodePattern || r == BareNumberPattern)
            {
                if (value is 480 or 576 or 720 or 1080 or 2160 || value is >= 1900 and <= 2099) continue;
            }
            episode = value;
            break;
        }
        return (season, episode);
    }

    /// <summary>对多段文本合并提取季集标记（调用方按「文件名优先、目录段从深到浅」传入）</summary>
    /// <remarks>Season / Episode 各取第一个非空命中——覆盖「Subs/S2/EP01.srt」季在目录段、集在文件名的拆分结构。</remarks>
    public static (int? Season, int? Episode) DetectSeasonEpisode(IEnumerable<string> texts)
    {
        int? season = null, episode = null;
        foreach (string text in texts)
        {
            (int? s, int? e) = DetectSeasonEpisode(text);
            season ??= s;
            episode ??= e;
            if (season is not null && episode is not null) break;
        }
        return (season, episode);
    }

    /// <summary>判断字幕检出的季集标记是否归属目标集</summary>
    /// <remarks>
    /// 集号必须命中（episode ~ episodeEnd 区间，覆盖双集合并归档 SxxEaa-Ebb）；
    /// 字幕无季号视为同季（「EP01.srt」常省略季），有季号则必须相等（防跨季同集号误配）。
    /// </remarks>
    public static bool MatchesEpisode(int? subSeason, int? subEpisode, int season, int episode, int episodeEnd)
    {
        if (subEpisode is null) return false;
        int end = Math.Max(episode, episodeEnd);
        if (subEpisode.Value < episode || subEpisode.Value > end) return false;
        if (subSeason is not null && subSeason.Value != season) return false;
        return true;
    }

    private const RegexOptions DetectOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>SxxEyy / SxxEPyy / Sxx.Eyy / Sxx Eyy（季+集一体）</summary>
    private static readonly Regex SeasonEpisodePattern = new(
        @"(?<![A-Za-z0-9])S(\d{1,2})\s*[._\- ]?\s*E[Pp]?(\d{1,4})(?![0-9])", DetectOptions);

    /// <summary>仅季模式（第N季 / Season N / 独立 Sxx 词）</summary>
    private static readonly Regex[] SeasonOnlyPatterns =
    {
        new(@"第\s*(\d{1,2})\s*季", DetectOptions),
        new(@"(?<![A-Za-z0-9])Season\s*(\d{1,2})(?![0-9])", DetectOptions),
        new(@"(?<![A-Za-z0-9])S(\d{1,2})(?![0-9A-Za-z])", DetectOptions),
    };

    /// <summary>方括号集数 [NN]（动漫常见；弱模式，调用处排除分辨率/年份值）</summary>
    private static readonly Regex BracketEpisodePattern = new(@"\[(\d{1,4})\]", DetectOptions);

    /// <summary>整段纯数字（字幕子目录段「01」；弱模式，调用处排除分辨率/年份值）</summary>
    private static readonly Regex BareNumberPattern = new(@"^(\d{1,4})$", DetectOptions);

    /// <summary>仅集模式（强 → 弱排序：第N集 → EP01 → Episode N → [NN] → 纯数字段）</summary>
    private static readonly Regex[] EpisodeOnlyPatterns =
    {
        new(@"第\s*(\d{1,4})\s*[集话話]", DetectOptions),
        new(@"(?<![A-Za-z0-9])E[Pp]?\.?\s*(\d{1,4})(?![0-9])", DetectOptions),
        new(@"(?<![A-Za-z0-9])Episode\s*(\d{1,4})(?![0-9])", DetectOptions),
        BracketEpisodePattern,
        BareNumberPattern,
    };

    /// <summary>语言识别 token 表（顺序敏感：繁中要在简中之前判定，避免「TC」被误判为「C」）</summary>
    private static readonly (string Token, string Code)[] LanguageTokens =
    {
        // 繁体优先（TC / CHT / 繁 / Traditional / zh-TW / zh-Hant）— 必须在简体之前判定
        ("zh-TW", "zh-TW"), ("zh-Hant", "zh-TW"),
        ("CHT", "zh-TW"), ("TC", "zh-TW"), ("繁", "zh-TW"), ("Traditional", "zh-TW"),
        // 简体（含 ISO code 自身）
        ("CHS", "zh"), ("SC", "zh"), ("简", "zh"), ("Chinese", "zh"),
        ("zh-CN", "zh"), ("zh-Hans", "zh"), ("zh", "zh"),
        // 英文
        ("English", "en"), ("ENG", "en"), ("EN", "en"), ("en", "en"),
        // 日文
        ("Japanese", "ja"), ("JPN", "ja"), ("JP", "ja"), ("ja", "ja"),
    };
}

/// <summary>字幕候选输入</summary>
/// <param name="FullPath">完整路径（IFileMover 会用）</param>
/// <param name="FileName">仅文件名（语言识别 / 扩展名判定用）</param>
/// <param name="SizeBytes">字节数（同语言多文件取最大）</param>
/// <param name="PathHint">相对源目录的中间目录段（'/' 分隔，如 "Subs/CHS"）；文件名识别不出语言时按段回退识别。视频同目录字幕为 null</param>
public sealed record SubtitleCandidate(string FullPath, string FileName, long SizeBytes, string? PathHint = null);

/// <summary>字幕重命名决策</summary>
/// <param name="Source">原始候选</param>
/// <param name="TargetFileName">目标文件名（Action=Rename 时非空）；不含目录</param>
/// <param name="Action">动作：Rename / SkipArchive / SkipUnsupported</param>
/// <param name="Reason">Skip 原因（写入日志）</param>
public sealed record SubtitleRenameDecision(
    SubtitleCandidate Source,
    string? TargetFileName,
    SubtitleAction Action,
    string? Reason);

public enum SubtitleAction
{
    Rename,
    SkipArchive,
    SkipUnsupported,
}
