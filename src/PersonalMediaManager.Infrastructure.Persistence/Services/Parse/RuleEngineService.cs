using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>规则引擎服务（D7.2）— 用户规则按优先级 → 内置规则兜底</summary>
/// <remarks>
/// 需求文档 §3.3.1 完整实现：噪声清洗 + 季集模式 + 年份提取 + 类型推断 + 父目录上下文加成 + 置信度评分表。
/// 用户规则（Parse_Rule）按 Priority 升序 + Enabled=true，命名捕获 title/year/season/episode；
/// 一条用户规则命中即返回（先命中先采用）；零命中走内置规则。
///
/// 正则安全：所有正则强制 RegexOptions.Compiled + MatchTimeout=500ms 防 ReDoS（需求文档 §3.3.1）；
/// 单条规则编译失败 / 匹配超时 → 仅写 Warning 跳过该规则，继续尝试下一条。
///
/// 置信度评分表（需求文档 §3.3.1）：
///   title+type+season+episode+year → 0.95
///   title+type+season+episode（无 year） → 0.85
///   title+type+year（电影无季集） → 0.85
///   title+type（缺季集或年份） → 0.70
///   仅 title → 0.50
///   啥都没有 → 0.10
///
/// 剧集硬约束（按缺失字段分档）：
///   tv 缺 episode → 0.50 触发 AI 兜底——集号无法靠 TMDB 反推，让 AI 从父目录 / 全路径段重新挖；
///   tv 仅缺 season（episode 在）→ 0.70 走 TMDB 直查——下游「单季自动补季」自动定 S01、
///   多季剧转人工审核选季，均优于为一个大概率是 1 的季号动用 AI。
///   Plex 命名规范要求剧集必有 SxxExx（ArchiveService 缺字段会抛 BusinessException），
///   两档取值都保证缺口在归档前由 AI / 自动补季 / 人工审核补全，不会直接 Failed。
///
/// 特殊字符判定：标题既有 CJK 又有拉丁字符（各 ≥ 3 个字符）→ HasSpecialChars=true，
/// 上层 ProcessFileService 据此触发 §3.3.2「直接走 AI」分支。
/// </remarks>
internal sealed class RuleEngineService : IRuleEngineService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private const RegexOptions BaseOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // 内置正则全部下沉到 BuiltinRulesCatalog 作为唯一来源，
    // 该 catalog 同时供 /parse-rules/builtin 端点把规则透出到 UI 让用户感知到内置规则的存在。

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly ILogger<RuleEngineService> _logger;

    public RuleEngineService(IDbContextFactory<PmmDbContext> dbFactory, ILogger<RuleEngineService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<RuleParseResult> ParseAsync(FileParseContext context, CancellationToken ct = default)
    {
        // 1. 用户规则按 Priority 升序
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<ParseRule> userRules = await db.ParseRules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);

        foreach (ParseRule rule in userRules)
        {
            RuleParseResult? hit = TryUserRule(rule, context);
            if (hit is not null)
            {
                _logger.LogDebug("用户规则命中：RuleId={RuleId}, Name={Name}", rule.Id, rule.Name);
                return WithAlternativeTitles(PostProcessSeasonMarkers(hit), context);
            }
        }

        // 2. 内置规则
        return WithAlternativeTitles(PostProcessSeasonMarkers(ApplyBuiltinRules(context)), context);
    }

    // ---------- 用户规则 ----------

    private RuleParseResult? TryUserRule(ParseRule rule, FileParseContext context)
    {
        // 编译规则正则一次，对所有候选 input 复用
        Regex re;
        try
        {
            re = new Regex(rule.Pattern, BaseOptions, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "用户规则正则非法（跳过）：RuleId={RuleId}", rule.Id);
            return null;
        }

        // 按 scope 枚举候选 input（AllAncestors 多 input 内→外逐段尝试，其他 scope 单 input）
        Match? matched = null;
        string matchedInput = string.Empty;
        foreach (string input in EnumerateInputs(rule.Scope, context))
        {
            if (string.IsNullOrEmpty(input)) continue;
            try
            {
                Match m = re.Match(input);
                if (m.Success) { matched = m; matchedInput = input; break; }
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger.LogWarning(ex, "用户规则匹配超时（跳过）：RuleId={RuleId}", rule.Id);
                return null;
            }
        }
        if (matched is null) return null;

        string fileName = context.FileName;
        string? parentFolderName = context.DirectParentFolderName;
        Match capt = matched;

        string? title = TryGroup(capt, "title");
        int? year = TryParseInt(TryGroup(capt, "year"));
        int? season = ParseCjkSeason(TryGroup(capt, "season"));
        string? seasonTitle = TryGroup(capt, "seasonTitle");
        int? episode = TryParseInt(TryGroup(capt, "episode"));
        int? episodeEnd = TryParseInt(TryGroup(capt, "episodeEnd"));
        // 范围非法（end < start）→ 丢弃 end 字段，保留 start 单值
        if (episodeEnd is int e && episode is int s && e < s) episodeEnd = null;
        // 用户规则同样做小数集守护（与内置提取器口径一致，检测逻辑同源）：最后一个集号捕获组末尾
        // 在原文中紧跟 .数字（总集篇/回顾篇，如「番名 - 11.5 [1080p]」）说明正则截走了小数集的整数
        // 部分，丢弃集号转低置信走 AI/审核，防止特别篇顶替正片集号；.1080p 多位数字、.5v2 数字后
        // 跟字母、年份形态均不误判。改在提取结果层而非种子 Pattern：给 Pattern 加负向断言会因
        // title 懒惰组回溯吞掉「11.」反而产出 episode=5，且结果层守护对用户自定义规则一并生效。
        if (episode is not null)
        {
            Group lastDigits = capt.Groups["episodeEnd"].Success ? capt.Groups["episodeEnd"] : capt.Groups["episode"];
            if (HasFractionalEpisodeTail(matchedInput, lastDigits.Index + lastDigits.Length))
            {
                episode = null;
                episodeEnd = null;
            }
        }

        string mediaType;
        if (rule.ForceType && !string.IsNullOrEmpty(rule.DefaultType))
        {
            mediaType = rule.DefaultType.ToLowerInvariant();
        }
        else
        {
            mediaType = InferMediaType(season, episode, rule.DefaultType, parentFolderName, matchedInput, year);
        }

        if (title is not null)
        {
            // 用户规则捕获的 title 也要做分隔符折叠（. _ - → 空格）+ 空白归一化，
            // 否则形如 `[字幕组][Jujutsu_Kaisen][59]` 会把 `Jujutsu_Kaisen` 直接送进 TMDB 搜索失准。
            // 这里只折叠分隔符，不剥噪声/方括号——用户既然显式捕获了 title 段，就尊重其内容范围。
            title = Normalize(SafeReplace(BuiltinRulesCatalog.Separator, title, " "));
        }
        title ??= CleanedStem(Path.GetFileNameWithoutExtension(fileName));
        bool special = HasMixedCjkLatin(title);

        double baseConf = ScoreConfidence(title, mediaType, season, episode, year);
        double finalConf = Math.Min(1.0, baseConf + Math.Max(0.0, rule.ConfidenceBonus));

        return new RuleParseResult(title, year, mediaType, season, episode, episodeEnd, finalConf, special, rule.Id, SeasonTitle: seasonTitle);
    }

    /// <summary>按 ParseScope 枚举返回该规则的候选输入字符串序列</summary>
    /// <remarks>
    /// 单元素 scope（FileName / ParentFolder / FullPath / RelativePath）yield 单值；
    /// AllAncestors 内→外逐层 yield（fileName-without-ext 优先，然后直接父、祖父、最外层），
    /// 调用方按 yield 顺序逐个尝试，先命中先返回 —— 越靠内层置信度越高（隐式排序，不需要外层加权）。
    /// </remarks>
    private static IEnumerable<string> EnumerateInputs(ParseScope scope, FileParseContext context)
    {
        string fileName = context.FileName;
        string fileNameStem = Path.GetFileNameWithoutExtension(fileName);
        string? parentFolderName = context.DirectParentFolderName;

        switch (scope)
        {
            case ParseScope.ParentFolder:
                yield return parentFolderName ?? string.Empty;
                yield break;

            case ParseScope.FullPath:
                // 兼容旧值：直接父目录 + 文件名拼接（保持与旧实现一致语义）
                yield return Path.Combine(parentFolderName ?? string.Empty, fileName);
                yield break;

            case ParseScope.RelativePath:
                // 标准化为 / 分隔的整条相对路径（含文件名，便于跨平台规则书写）
                List<string> parts = new(context.RelativeSegments.Count + 1);
                parts.AddRange(context.RelativeSegments);
                parts.Add(fileName);
                yield return string.Join('/', parts);
                yield break;

            case ParseScope.AllAncestors:
                // 内→外：文件名 stem → 直接父 → 祖父 → ... → 最外层
                yield return fileNameStem;
                for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
                {
                    yield return context.RelativeSegments[i];
                }
                yield break;

            case ParseScope.FileName:
            default:
                yield return fileName;
                yield break;
        }
    }

    // ---------- 内置规则 ----------

    /// <summary>内置规则执行：按「内→外」遍历路径段，逐段补全 season/episode/episodeEnd/year/title</summary>
    /// <remarks>
    /// 候选层级（内→外）：
    ///   [0] = 文件名 stem（最优先；技术细节最完整）
    ///   [1] = 直接父目录
    ///   [2] = 祖父目录
    ///   [...] = 一直到监控根下的第一级
    ///
    /// 字段填充顺序（先内后外，缺失字段才用更外层补）：
    ///   season / episode / episodeEnd → 内层优先（实际文件命名最具体）
    ///   year → 内层优先（剧集文件常无年份，需从父目录补）
    ///   title → 选择「**剥噪声后**最有信息量」的层段：剥掉季/集/年份/噪声后，剩余非空且至少含一个 CJK 或拉丁字母词的最外层（PT 站父目录承载剧名 + 文件名只剩 SxxExx 时，这一策略保证从父目录拿到剧名）。
    /// </remarks>
    private static RuleParseResult ApplyBuiltinRules(FileParseContext context)
    {
        string fileName = context.FileName;
        string stem = Path.GetFileNameWithoutExtension(fileName);

        // 按「内→外」组装候选层级
        List<string> layers = new(context.RelativeSegments.Count + 1) { stem };
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
        {
            layers.Add(context.RelativeSegments[i]);
        }

        // 逐字段「内层优先」提取
        int? season = null;
        int? episode = null;
        int? episodeEnd = null;
        int? year = null;
        string? seasonTitle = null;
        foreach (string layer in layers)
        {
            (int? s, int? e, int? eEnd) = ExtractSeasonEpisode(layer);
            season ??= s;
            episode ??= e;
            episodeEnd ??= eEnd;
            year ??= ExtractYear(layer);
            seasonTitle ??= ExtractSeasonTitle(layer);
            if (season is not null && episode is not null && year is not null) break;
        }

        // 兜底：文件名 stem 是纯 1-3 位数字（如 01 / 02 / 100），把它当 episode
        // 限制 1-3 位避免与 4 位年份（1900-2099）混淆；4 位数字若超出年份范围（如 0815、9999）也接受
        if (episode is null && stem.Length is >= 1 and <= 4 && int.TryParse(stem, out int numericStem))
        {
            bool isYearLike = stem.Length == 4 && numericStem is >= 1900 and <= 2099;
            // 小数集守护（与 SxxEyy / EP 形态同口径）：无扩展名文件「11.5」会被 GetFileNameWithoutExtension
            // 把「.5」当扩展名剥掉、stem 截成「11」，不得再当集号——丢弃转低置信走 AI / 人工审核。
            // 带扩展名的「11.5.mkv」stem 为「11.5」，int.TryParse 天然拒绝小数形态，不会走进本兜底。
            bool isFractionalTruncated = HasFractionalEpisodeTail(Path.GetFileName(fileName), stem.Length);
            if (!isYearLike && !isFractionalTruncated)
            {
                episode = numericStem;
            }
        }

        // 兜底：整串「压制代号-集号」形态（DACZLNF-09 / YTYHXBYL-30）——字母段是压制组 / 缩写代号，
        // 数字段作集号（1900-2099 的 4 位年份除外，与纯数字 stem 兜底同口径）；该层不参与标题竞选（见 SelectTitle）
        if (episode is null)
        {
            Match tagEp = SafeMatch(BuiltinRulesCatalog.ReleaseTagEpisode, stem);
            if (tagEp.Success && TryParseInt(tagEp.Groups["episode"].Value) is int tagEpNum)
            {
                bool tagEpYearLike = tagEp.Groups["episode"].Value.Length == 4 && tagEpNum is >= 1900 and <= 2099;
                if (!tagEpYearLike) episode = tagEpNum;
            }
        }

        // 标题：从「最有信息量」的层选；优先级 = 剥噪声后非空 && 含字母词 → 越外层（PT 站剧名常在父目录）越优先
        (string title, bool titleMeaningful) = SelectTitle(layers, season, episode, year);

        // 类型推断时把所有层拼起来作为上下文（让父目录的「电影」「Anime」等 hint 也参与判断）
        string allContext = string.Join(' ', layers);
        string? parentFolderName = context.DirectParentFolderName;
        string mediaType = InferMediaType(season, episode, defaultType: null, parentFolderName, allContext, year);
        bool special = HasMixedCjkLatin(title);
        double confidence = ScoreConfidence(title, mediaType, season, episode, year);
        // 标题是兜底回退的 stem 原文（各层均无有效标题内容）时压低置信度：残渣 / 代号标题直查 TMDB
        // 只会零候选或误命中，压到阈值以下让流程走「本地备选标题重搜 → AI 兜底」链
        if (!titleMeaningful) confidence = Math.Min(confidence, 0.50);

        return new RuleParseResult(title, year, mediaType, season, episode, episodeEnd, confidence, special, MatchedRuleId: null, SeasonTitle: seasonTitle);
    }

    /// <summary>从候选层级（内→外）中选出最适合做标题的一层</summary>
    /// <remarks>
    /// 策略：
    ///   1. 先尝试**文件名 stem**（layers[0]）：剥噪声后若仍含有效标题内容，直接采用（细节最完整）。
    ///      「压制代号-集号」整串形态（DACZLNF-09）的层直接跳过——字母段是代号不是标题。
    ///   2. 否则按内→外顺序找第一个「剥噪声后有标题信息量」的层。
    ///      这能正确处理 PT 站「父目录承载剧名 / 文件名只剩 SxxExx 或纯集号」的场景。
    ///   3. 全部失败 → 回退到 stem 原文并标记 Meaningful=false，由调用方压低置信度，
    ///      避免技术残渣标题（"2026 60fps WEB 1"）以高置信直查 TMDB 零候选后白走 AI。
    /// </remarks>
    private static (string Title, bool Meaningful) SelectTitle(IReadOnlyList<string> layers, int? season, int? episode, int? year)
    {
        for (int i = 0; i < layers.Count; i++)
        {
            string raw = layers[i];
            // 「压制代号-集号」整串层：字母段是压制组 / 缩写代号，跳过竞选让更外层（父目录）接手
            if (IsReleaseTagEpisodeLayer(raw)) continue;
            string cleaned = ExtractTitle(raw, season, episode, year);
            if (HasMeaningfulContent(cleaned))
            {
                return (cleaned, true);
            }
        }
        // 兜底：第一层（stem）原文
        return (layers.Count > 0 ? layers[0] : string.Empty, false);
    }

    /// <summary>该层是否为「压制代号-集号」整串形态（数字段为 4 位年份的不算，如 Show-2020 是「标题-年份」）</summary>
    private static bool IsReleaseTagEpisodeLayer(string layer)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.ReleaseTagEpisode, layer);
        if (!m.Success) return false;
        string ep = m.Groups["episode"].Value;
        return !(ep.Length == 4 && TryParseInt(ep) is >= 1900 and <= 2099);
    }

    /// <summary>剥噪声后的字符串是否「有标题信息量」：剔除纯数字与单字符 token 后仍有含 ≥2 个字母/CJK 的 token</summary>
    /// <remarks>
    /// 旧实现只数全串字母总量，技术残渣（"2026 60fps WEB 1" 的 fps / WEB）也能凑够 2 个字母被误判有效，
    /// 把父目录里的真实剧名挤出标题竞选。技术词主体由 Noise 词表剥除，这里按 token 粒度做第二道防线：
    /// 纯数字 token（年份 / 集号残留）与单字符 token（分隔残渣）不算信息量。
    /// </remarks>
    private static bool HasMeaningfulContent(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (string token in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2) continue;
            int letterCount = 0;
            bool allDigits = true;
            foreach (char ch in token)
            {
                if (IsCjk(ch) || IsLatinLetter(ch)) letterCount++;
                if (!char.IsDigit(ch)) allDigits = false;
            }
            if (allDigits) continue;
            if (letterCount >= 2) return true;
        }
        return false;
    }

    /// <summary>从单段字符串提取 season/episode/episodeEnd（按 SxxExx → 中文集号 → EP/E → 方括号 → 中文季 顺序）</summary>
    private static (int? season, int? episode, int? episodeEnd) ExtractSeasonEpisode(string s)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.SeasonEpisodeLatin, s);
        if (m.Success)
        {
            // 小数集守护：SxxEyy 紧跟「.单数字 + 分隔符/结尾」（如 S01E11.5.Recap）是动漫回顾/特别篇惯例，
            // 截成整数集 E11 会与正片第 11 集同号高置信直通归档（Overwrite 策略下最坏覆盖正片）。
            // 此处丢弃集号仅保留季号 → 下游「tv 缺集号」评分守护压到 0.50，确保走 AI / 人工审核。
            if (HasFractionalEpisodeTail(s, m))
            {
                return (TryParseInt(m.Groups["season"].Value), null, null);
            }
            int? eEnd = TryParseInt(m.Groups["episodeEnd"].Value);
            int? ep = TryParseInt(m.Groups["episode"].Value);
            if (eEnd is int e && ep is int sp && e < sp) eEnd = null;
            return (TryParseInt(m.Groups["season"].Value), ep, eEnd);
        }

        int? season = null;
        Match sm = SafeMatch(BuiltinRulesCatalog.SeasonChinese, s);
        if (sm.Success) season = ParseCjkSeason(sm.Groups["season"].Value);

        // 季号兜底：独立 Sxx（如父目录 Born.with.Luck.S01.2026）
        if (season is null)
        {
            Match som = SafeMatch(BuiltinRulesCatalog.SeasonOnlyLatin, s);
            if (som.Success) season = TryParseInt(som.Groups["season"].Value);
        }

        // 季号兜底：英文全词「Season NN」（标准目录布局 Show (2020)/Season 02/07.mkv 的季目录层；
        // 本方法按层级逐段调用，文件名层带「Season N」时同样生效）
        if (season is null)
        {
            Match swm = SafeMatch(BuiltinRulesCatalog.SeasonWordLatin, s);
            if (swm.Success) season = TryParseInt(swm.Groups["season"].Value);
        }

        // 罗马数字季号兜底（标题尾部 II-X，主要动漫）：仅在中文 / 拉丁季号均未命中时尝试
        if (season is null)
        {
            Match rm = SafeMatch(BuiltinRulesCatalog.SeasonRoman, s);
            if (rm.Success) season = ParseRomanSeason(rm.Groups["roman"].Value);
        }

        (int? ep2, int? eEnd2) = ExtractEpisodeWithEnd(s);
        return (season, ep2, eEnd2);
    }

    private static (int? episode, int? episodeEnd) ExtractEpisodeWithEnd(string s)
    {
        foreach (Regex re in new[]
        {
            BuiltinRulesCatalog.EpisodeChinese,
            BuiltinRulesCatalog.EpisodeOnly,
            BuiltinRulesCatalog.BracketEpisode,
        })
        {
            Match m = SafeMatch(re, s);
            if (m.Success)
            {
                // 小数集守护（与 SxxEyy 同口径）：集号数字紧跟「.单数字 + 分隔符/结尾」（如 EP11.5 回顾集）
                // 是半集惯例，截成整数集会与正片同号冲突（父目录补出季号时还会以 0.85 直通归档）；
                // 丢弃本层集号转低置信走 AI / 人工审核。注意从「最后一个数字捕获组末尾」起检测而非 Match
                // 末尾——EpisodeOnly 的尾部分隔符是消耗组（「EP11.5」命中文本为「EP11.」，小数点已被吃进 Match）。
                Group lastDigits = m.Groups["episodeEnd"].Success ? m.Groups["episodeEnd"] : m.Groups["episode"];
                if (HasFractionalEpisodeTail(s, lastDigits.Index + lastDigits.Length))
                {
                    return (null, null);
                }
                int? ep = TryParseInt(m.Groups["episode"].Value);
                int? eEnd = TryParseInt(m.Groups["episodeEnd"].Value);
                if (eEnd is int e && ep is int sp && e < sp) eEnd = null;
                return (ep, eEnd);
            }
        }
        return (null, null);
    }

    /// <summary>SxxExx 命中后检测集号是否带小数尾巴（Match 末尾即集号数字末尾的形态用）</summary>
    /// <remarks>SeasonEpisodeLatin 的 Match 以集号数字收尾，可直接以 Match 末尾作为检测起点。</remarks>
    private static bool HasFractionalEpisodeTail(string s, Match m) =>
        HasFractionalEpisodeTail(s, m.Index + m.Length);

    /// <summary>检测 pos（集号数字串末尾）起是否为小数集尾巴「.单数字」（如 E11.5 / EP11.5 回顾/特别篇形态）</summary>
    /// <remarks>
    /// 形态要求「.」+ 恰好 1 位数字 + 分隔符或结尾，三道约束排除技术 token 误判：
    ///   · .1080p / .2008（多位数字）→ 不是小数尾巴；
    ///   · .4K / .5v2（数字后跟字母）→ 不是小数尾巴；
    ///   · .5.Recap / 串尾 .5 → 命中。
    /// 命中后由调用方丢弃集号转低置信（AI / 人工审核），避免 E11.5 截成 E11 与正片冲突。
    /// </remarks>
    private static bool HasFractionalEpisodeTail(string s, int pos)
    {
        if (pos + 1 >= s.Length || s[pos] != '.') return false;
        if (!char.IsAsciiDigit(s[pos + 1])) return false;
        if (pos + 2 >= s.Length) return true; // 「.5」收尾
        char next = s[pos + 2];
        return next is '.' or '-' or '_' or ' ' or '\t';
    }

    private static int? ExtractYear(string s)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.Year, s);
        return m.Success ? TryParseInt(m.Groups["year"].Value) : null;
    }

    private static string ExtractTitle(string stem, int? season, int? episode, int? year)
    {
        string s = CleanedStem(stem);

        // 年份 / 季集是常见的「标题边界」：取其前的部分作为标题，丢弃后续 release group 等噪声。
        // 边界在串首（boundary=0，如「S01E01.2026.…」无标题打头的命名）→ 标题为空，
        // 交由 SelectTitle 回落到更外层（父目录常承载剧名），而不是把边界后的技术残渣当标题
        int boundary = FindEarliestBoundary(s, year);
        if (boundary >= 0)
        {
            s = s[..boundary];
        }
        else
        {
            // 没有边界时，仍剥季集字段
            s = SafeReplace(BuiltinRulesCatalog.SeasonEpisodeLatin, s, string.Empty);
            s = SafeReplace(BuiltinRulesCatalog.SeasonChinese, s, string.Empty);
            s = SafeReplace(BuiltinRulesCatalog.SeasonOnlyLatin, s, string.Empty);
            s = SafeReplace(BuiltinRulesCatalog.EpisodeChinese, s, string.Empty);
            s = SafeReplace(BuiltinRulesCatalog.EpisodeOnly, s, string.Empty);
            s = SafeReplace(BuiltinRulesCatalog.BracketEpisode, s, string.Empty);
        }

        return Normalize(s);
    }

    /// <summary>找出年份 / 季集中最早出现的位置，作为标题边界（之后内容视为噪声）</summary>
    private static int FindEarliestBoundary(string s, int? year)
    {
        int best = -1;
        void Consider(Match m)
        {
            if (m.Success && m.Index >= 0 && (best == -1 || m.Index < best))
            {
                best = m.Index;
            }
        }

        if (year is not null) Consider(SafeMatch(BuiltinRulesCatalog.Year, s));
        Consider(SafeMatch(BuiltinRulesCatalog.SeasonEpisodeLatin, s));
        Consider(SafeMatch(BuiltinRulesCatalog.SeasonChinese, s));
        Consider(SafeMatch(BuiltinRulesCatalog.SeasonOnlyLatin, s));
        Consider(SafeMatch(BuiltinRulesCatalog.EpisodeChinese, s));
        Consider(SafeMatch(BuiltinRulesCatalog.EpisodeOnly, s));
        Consider(SafeMatch(BuiltinRulesCatalog.BracketEpisode, s));
        // 篇章标题与罗马数字季号也是标题边界（鬼灭之刃 锻刀村篇 / 刀剑神域II → 标题截到边界前）
        Consider(SafeMatch(BuiltinRulesCatalog.SeasonArc, s));
        Consider(SafeMatch(BuiltinRulesCatalog.SeasonRoman, s));

        return best;
    }

    private static string CleanedStem(string stem)
    {
        // 顺序敏感：先剥发布组尾缀（依赖 - 边界），再剥方括号 / 全角【】块，
        // 然后剥总季数/总集数后缀——必须在分隔符折叠之前，否则「第1-6季」的连字符被折叠成空格后无法识别区间；
        // 「Season NN」全词季号须在通用噪声之前整体剥除（Noise 词表含裸 season 单词，先跑会只吃掉单词、
        // 把季号数字残留进标题，如「Show Season 2」→「Show 2」），最后剥噪声 token + 分隔符。
        string s = SafeReplace(BuiltinRulesCatalog.ReleaseGroupSuffix, stem, " ");
        s = SafeReplace(BuiltinRulesCatalog.GroupBracket, s, " ");
        s = SafeReplace(BuiltinRulesCatalog.TotalCountNoise, s, " ");
        s = SafeReplace(BuiltinRulesCatalog.SeasonWordLatin, s, " ");
        s = SafeReplace(BuiltinRulesCatalog.Noise, s, " ");
        s = SafeReplace(BuiltinRulesCatalog.Separator, s, " ");
        return Normalize(s);
    }

    private static string Normalize(string s)
    {
        s = s.Trim();
        // 折叠多空格
        StringBuilder sb = new(s.Length);
        bool prevSpace = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static string InferMediaType(int? season, int? episode, string? defaultType, string? parentFolderName, string anyContext, int? year = null)
    {
        // 明确季集 → tv
        if (season is not null || episode is not null) return "tv";

        if (!string.IsNullOrEmpty(defaultType))
        {
            string d = defaultType.ToLowerInvariant();
            if (d is "movie" or "tv") return d;
        }

        string ctx = ((parentFolderName ?? string.Empty) + " " + (anyContext ?? string.Empty)).ToLowerInvariant();
        if (ContainsAny(ctx, "tv", "剧集", "anime", "动漫", "season", "series") &&
            !ContainsAny(ctx, "movie", "电影"))
        {
            return "tv";
        }
        if (ContainsAny(ctx, "movie", "电影", "film"))
        {
            return "movie";
        }
        // 有年份但无任何剧集标记 → 视为电影（最常见命名模式）
        if (year is not null) return "movie";
        return "unknown";
    }

    private static bool ContainsAny(string s, params string[] needles)
    {
        foreach (string n in needles)
        {
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------- 置信度 ----------

    /// <summary>按需求文档 §3.3.1 评分表打分</summary>
    private static double ScoreConfidence(string? title, string mediaType, int? season, int? episode, int? year)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        if (!hasTitle && season is null && episode is null && year is null && mediaType == "unknown")
        {
            return 0.10;
        }
        if (!hasTitle) return 0.10;

        bool hasType = mediaType is "movie" or "tv";
        bool hasSeason = season is not null;
        bool hasEpisode = episode is not null;
        bool hasYear = year is not null;

        if (hasType && hasSeason && hasEpisode && hasYear) return 0.95;
        if (hasType && hasSeason && hasEpisode) return 0.85;
        if (hasType && hasYear && mediaType == "movie") return 0.85;
        // 剧集缺集号 → 压到 0.50 触发 AI 兜底：集号无法靠 TMDB 反推，需要 AI 从路径上下文再挖
        if (mediaType == "tv" && !hasEpisode) return 0.50;
        // 剧集仅缺季号（集号在）→ 0.70 走 TMDB 直查：下游「单季自动补季」可自动定 S01，
        // 多季剧转人工审核选季——两者都优于为一个大概率是 1 的季号动用 AI（还可能猜错）
        if (mediaType == "tv" && !hasSeason) return 0.70;
        if (hasType) return 0.70;
        return 0.50;
    }

    // ---------- 特殊字符（CJK + Latin 混杂）----------

    /// <summary>标题既有 ≥ 3 个 CJK 字符 又有 ≥ 3 个拉丁字符 → 命名混杂，触发 AI 走</summary>
    internal static bool HasMixedCjkLatin(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int cjk = 0, latin = 0;
        foreach (char ch in s)
        {
            if (IsCjk(ch)) cjk++;
            else if (IsLatinLetter(ch)) latin++;
        }
        return cjk >= 3 && latin >= 3;
    }

    private static bool IsCjk(char ch) =>
        (ch is >= '一' and <= '鿿') ||   // CJK Unified
        (ch is >= '぀' and <= 'ゟ') ||   // Hiragana
        (ch is >= '゠' and <= 'ヿ') ||   // Katakana
        (ch is >= '가' and <= '힯');     // Hangul

    private static bool IsLatinLetter(char ch) =>
        (ch is >= 'a' and <= 'z') || (ch is >= 'A' and <= 'Z');

    // ---------- helpers ----------

    private static Match SafeMatch(Regex re, string s)
    {
        try { return re.Match(s); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    private static string SafeReplace(Regex re, string s, string replacement)
    {
        try { return re.Replace(s, replacement); }
        catch (RegexMatchTimeoutException) { return s; }
    }

    private static string? TryGroup(Match m, string name)
    {
        Group g = m.Groups[name];
        return g.Success && g.Value.Length > 0 ? g.Value : null;
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, out int v) ? v : null;

    // ---------- 本地备选搜索标题 ----------

    /// <summary>为解析结果挂载本地备选搜索标题（主标题拆分段 + 其余路径层标题）</summary>
    /// <remarks>
    /// 供 ProcessFileService 在「主标题 TMDB 不中 / 混排 / 低置信」将触发 AI 兜底之前逐个重搜 TMDB，
    /// 命中即免走 AI（典型场景：英文主标题在 TMDB zh-CN 搜不到，但路径目录里带中文剧名）。
    /// 顺序即搜索尝试顺序：纯 CJK 段优先（TMDB 首选语言 zh-CN 命中率最高），
    /// 组内按发现顺序（主标题拆分段 → 路径层内→外）。归一化去重（含与主标题比对），上限 5 个。
    /// </remarks>
    private static RuleParseResult WithAlternativeTitles(RuleParseResult result, FileParseContext context)
    {
        List<string> alts = BuildAlternativeTitles(result, context);
        return alts.Count == 0 ? result : result with { AlternativeTitles = alts };
    }

    private const int MaxAlternativeTitles = 5;

    private static List<string> BuildAlternativeTitles(RuleParseResult result, FileParseContext context)
    {
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            string normalized = NormalizeForDedup(candidate);
            if (normalized.Length < 2) return;
            if (!seen.Add(normalized)) return;
            ordered.Add(candidate.Trim());
        }

        // 主标题只入去重集不入结果——主流程已用它搜索过
        seen.Add(NormalizeForDedup(result.Title));

        // 1. 主标题的混排拆分段（CJK 段 / 粘连副标段 / 拉丁词组段）
        foreach (string seg in SplitMixedSegments(result.Title)) Add(seg);

        // 2. 各路径层（stem + 目录段内→外）独立提取标题，整段与拆分段都作候选
        string stem = Path.GetFileNameWithoutExtension(context.FileName);
        List<string> layers = new(context.RelativeSegments.Count + 1) { stem };
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--) layers.Add(context.RelativeSegments[i]);
        foreach (string layer in layers)
        {
            // 「压制代号-集号」层无标题价值（与 SelectTitle 同口径）
            if (IsReleaseTagEpisodeLayer(layer)) continue;
            string cleaned = ExtractTitle(layer, result.Season, result.Episode, result.Year);
            if (!HasMeaningfulContent(cleaned)) continue;
            Add(cleaned);
            foreach (string seg in SplitMixedSegments(cleaned)) Add(seg);
        }

        if (ordered.Count <= 1) return ordered;

        // 纯 CJK 段整体前移（OrderByDescending 稳定排序，组内保持发现顺序），再截断上限
        return ordered
            .OrderByDescending(IsAllCjkTitle)
            .Take(MaxAlternativeTitles)
            .ToList();
    }

    /// <summary>把混排标题拆成可独立搜索的子段：CJK 段 / 与 CJK 粘连的拉丁副标段 / 拉丁词组段</summary>
    /// <remarks>
    /// 「机动战士高达SEEDFREEDOM Mobile Suit Gundam Seed Freedom」→
    /// [「机动战士高达」,「SEEDFREEDOM」,「Mobile Suit Gundam Seed Freedom」]。
    /// 紧贴 CJK 无空格的拉丁 run（SEEDFREEDOM）常是与中文名粘连的系列副标，单独成段，
    /// 避免混进空格词组污染整组搜索词。CJK 段 ≥2 字、拉丁段 ≥4 字母才有搜索价值；
    /// 纯单语标题拆出的唯一段与原串相同，由调用方去重剔除。
    /// </remarks>
    internal static List<string> SplitMixedSegments(string title)
    {
        List<string> segments = [];
        if (string.IsNullOrWhiteSpace(title)) return segments;

        // 1. 纯 CJK 段（连续 ≥2 字）
        StringBuilder cjkRun = new();
        foreach (char ch in title)
        {
            if (IsCjk(ch))
            {
                cjkRun.Append(ch);
                continue;
            }
            if (cjkRun.Length >= 2) segments.Add(cjkRun.ToString());
            cjkRun.Clear();
        }
        if (cjkRun.Length >= 2) segments.Add(cjkRun.ToString());

        // 2. 拉丁侧：把 CJK 全部替为哨兵后按哨兵切片；片首紧贴哨兵（无空格）的首词 = 粘连副标，单独成段
        const char sentinel = '\u0001';
        StringBuilder masked = new(title.Length);
        foreach (char ch in title) masked.Append(IsCjk(ch) ? sentinel : ch);
        string[] pieces = masked.ToString().Split(sentinel);
        for (int i = 0; i < pieces.Length; i++)
        {
            string piece = pieces[i];
            if (piece.Length == 0) continue;
            string phrase = piece;
            if (i > 0 && !char.IsWhiteSpace(piece[0]))
            {
                int space = piece.IndexOf(' ');
                string glued = space < 0 ? piece : piece[..space];
                phrase = space < 0 ? string.Empty : piece[(space + 1)..];
                if (CountLatinLetters(glued) >= 4) segments.Add(glued.Trim());
            }
            phrase = phrase.Trim();
            if (CountLatinLetters(phrase) >= 4) segments.Add(phrase);
        }
        return segments;
    }

    /// <summary>标题是否为纯 CJK 段（含 CJK 且不含拉丁字母；空格 / 数字点缀允许）</summary>
    private static bool IsAllCjkTitle(string s)
    {
        bool hasCjk = false;
        foreach (char ch in s)
        {
            if (IsCjk(ch))
            {
                hasCjk = true;
                continue;
            }
            if (IsLatinLetter(ch)) return false;
        }
        return hasCjk;
    }

    private static int CountLatinLetters(string s)
    {
        int n = 0;
        foreach (char ch in s)
        {
            if (IsLatinLetter(ch)) n++;
        }
        return n;
    }

    /// <summary>备选标题去重归一化：去空白 + 小写（与文件夹复用守门同口径）</summary>
    private static string NormalizeForDedup(string s)
    {
        StringBuilder sb = new(s.Length);
        foreach (char ch in s)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>季标记统一后处理：对最终标题补「篇章名」与「尾部罗马数字季号」</summary>
    /// <remarks>
    /// 内置规则路径已在解析阶段提取过的字段此处不重复（仅缺失才补），保证两条规则路径口径一致：
    /// 即便命中用户规则（其 title 可能残留「II」「XXX篇」），也能补出 season / seasonTitle 并清理标题，
    /// 避免「Sword Art Online II」「鬼灭之刃 锻刀村篇」整串送 TMDB 失准。
    /// 罗马数字补季后视为剧集；电影续集（教父 II）误判由下游 TMDB/AI 纠正回 movie（归档忽略 season）。
    /// </remarks>
    private static RuleParseResult PostProcessSeasonMarkers(RuleParseResult r)
    {
        string title = r.Title;
        int? season = r.Season;
        string? seasonTitle = r.SeasonTitle;
        bool changed = false;

        // 篇章标题（XXX篇）：未提取过才补；剥掉标题中篇章及其后内容
        if (seasonTitle is null && !string.IsNullOrEmpty(title))
        {
            Match arc = SafeMatch(BuiltinRulesCatalog.SeasonArc, title);
            if (arc.Success)
            {
                seasonTitle = arc.Groups["seasonTitle"].Value;
                title = title[..arc.Index];
                changed = true;
            }
        }

        // 尾部罗马数字季号（II-X）：仅季号缺失时补；剥掉标题中罗马数字及其后内容
        if (season is null && !string.IsNullOrEmpty(title))
        {
            Match rm = SafeMatch(BuiltinRulesCatalog.SeasonRoman, title);
            if (rm.Success)
            {
                season = ParseRomanSeason(rm.Groups["roman"].Value);
                title = title[..rm.Index];
                changed = true;
            }
        }

        if (!changed) return r;

        string normalized = Normalize(title);
        if (string.IsNullOrEmpty(normalized)) normalized = r.Title; // 剥空则回退原标题，避免标题丢失

        // 补了季号 → 视为剧集（罗马 / 篇章均剧集语义）；缺季时 type 不变（篇章可能无季号）
        string mediaType = season is not null && r.MediaType != "tv" ? "tv" : r.MediaType;
        // 季号补全后置信度可能从「缺季 0.50」升级；取与原值较高者，避免抹掉用户规则 bonus
        double confidence = Math.Max(r.Confidence, ScoreConfidence(normalized, mediaType, season, r.Episode, r.Year));
        bool special = HasMixedCjkLatin(normalized);

        return r with
        {
            Title = normalized,
            Season = season,
            SeasonTitle = seasonTitle,
            MediaType = mediaType,
            Confidence = confidence,
            HasSpecialChars = special,
        };
    }

    /// <summary>罗马数字季号 II-X → int（不含单 I；正则已限定 II 及以上）；非法返回 null</summary>
    private static int? ParseRomanSeason(string roman) => roman.ToUpperInvariant() switch
    {
        "II" => 2,
        "III" => 3,
        "IV" => 4,
        "V" => 5,
        "VI" => 6,
        "VII" => 7,
        "VIII" => 8,
        "IX" => 9,
        "X" => 10,
        _ => null,
    };

    /// <summary>提取季的篇章标题（中文「XXX篇」，如「锻刀村篇」）；无则 null</summary>
    private static string? ExtractSeasonTitle(string s)
    {
        Match m = SafeMatch(BuiltinRulesCatalog.SeasonArc, s);
        return m.Success ? m.Groups["seasonTitle"].Value : null;
    }

    private const string CjkDigits = "零一二三四五六七八九";

    /// <summary>解析季号：阿拉伯数字直转；中文数字（一 ~ 九十九，含「十」「两」）转 int；无法解析返回 null</summary>
    private static int? ParseCjkSeason(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (int.TryParse(s, out int n)) return n;
        return ParseCjkNumber1To99(s);
    }

    /// <summary>中文数字（1-99）转 int：个位「一~九」/「十」/「十几」/「几十」/「几十几」；越界或非法返回 null</summary>
    private static int? ParseCjkNumber1To99(string s)
    {
        int shi = s.IndexOf('十');
        if (shi < 0)
        {
            if (s.Length != 1) return null;
            int d = CjkDigit(s[0]);
            return d >= 1 ? d : null;
        }
        int tens = shi == 0 ? 1 : CjkDigit(s[0]);
        if (tens < 1) return null;
        int units = shi == s.Length - 1 ? 0 : CjkDigit(s[^1]);
        if (units < 0) return null;
        int v = tens * 10 + units;
        return v is >= 1 and <= 99 ? v : null;
    }

    /// <summary>单个中文数字字符 → 值（零~九 = 0~9，「两」= 2）；非中文数字字符返回 -1</summary>
    private static int CjkDigit(char c) => c == '两' ? 2 : CjkDigits.IndexOf(c);
}
