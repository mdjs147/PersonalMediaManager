using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

/// <summary>初始种子数据补齐实现（数据库设计 §3.10）</summary>
/// <remarks>
/// 幂等策略：按「整表为空」判断——分类表非空即视为用户已开始配置，整体跳过；解析规则表同理。
/// 这样不会与用户后续的增删改冲突，重启多次也只在首次空库时写入一次。
/// 例外——存量种子规则一次性修正（FixLegacyParseRulesAsync）：种子文本自身修过缺陷时
/// （如「全N集」不再把总集数捕获为 episode），按「字段精确等于旧种子值」识别用户从未改过的存量行
/// 并自动替换为新值；用户改过的行绝不触碰。每次启动都跑但天然幂等（旧值匹配不到即无事发生）。
/// 时间戳 / RowVersion 由 TimestampInterceptor / RowVersionInterceptor 自动维护，无需手填。
///
/// 三类种子：
///   - 分类（Category_Definition）：电影 / 电视剧 / 动漫，TargetRoot 占位 &lt;UNSET&gt; 待用户改。
///   - 分类匹配规则（Category_MatchRule）：按 Priority 升序「先命中先采用」，动漫 → 电视剧 → 电影兜底。
///   - 解析规则（Parse_Rule）：23 条内置规则全量种子（2026-06-11 迁移合并时自历史 migration 移植）。
/// </remarks>
internal sealed class DataSeeder : IDataSeeder
{
    /// <summary>分类目标根目录占位符；用户须在「设置-分类」改为实际路径后才能归档</summary>
    private const string UnsetRoot = "<UNSET>";

    // ----- 「全N集 整季合集」种子值（新值供种子写入；旧值仅供存量库一次性修正比对，勿改动） -----
    // 旧 Pattern 把「全N集」的 N（总集数）捕获为 episode，整季打包文件会被归档成第 N 集
    // （如「扫毒.全30集」→ S01E30 误指向第 30 集）；2026-06 解析规则组修复（418519d）改为不捕获集号，
    // 存量库中仍保留旧值的行由 FixLegacyParseRulesAsync 启动时自动跟进。
    private const string FullPackPattern = @"^(?<title>.+?)[\s\._\-]+全\d{1,4}集";
    private const string LegacyFullPackPattern = @"^(?<title>.+?)[\s\._\-]+全(?<episode>\d{1,4})集";
    private const string FullPackDescription = "整季合集「全N集」命名（如扫毒.全30集），识别为 tv 并清洗标题；总集数不作集号，交 AI / 人工审核定集";
    private const string LegacyFullPackDescription = "整季合集「全N集」命名（如扫毒.全30集），识别为 tv；episode 抓总集数仅作展示";

    // ----- 「OVA SP 特别篇」种子值（同批仅 Description 补充特别篇 Season 0 约定，Pattern 未变） -----
    private const string OvaPattern = @"^(?:\[[^\]]{1,40}\]\s*)*(?<title>[^\[\]]+?)[\s\._\-]+(?:OVA|SP|NCED|NCOP|番外|特典|映画|剧场版)[\s\._\-]?(?<episode>\d{1,3})(?![\d])";
    private const string OvaDescription = "OVA / SP / 番外 / 特典 等动漫特别篇标记，集号 1-3 位；季号留空交 AI 按特别篇约定归 Season 0";
    private const string LegacyOvaDescription = "OVA / SP / 番外 / 特典 等动漫特别篇标记，集号 1-3 位";

    private readonly IDbContextFactory<PmmDbContext> _dbFactory;
    private readonly ILogger<DataSeeder> _logger;

    public DataSeeder(IDbContextFactory<PmmDbContext> dbFactory, ILogger<DataSeeder> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using PmmDbContext ctx = await _dbFactory.CreateDbContextAsync(ct);
        await SeedCategoriesAsync(ctx, ct);
        await SeedParseRulesAsync(ctx, ct);
        await FixLegacyParseRulesAsync(ctx, ct);
    }

    /// <summary>补齐 5 个默认分类 + 对应匹配规则；分类表非空则跳过</summary>
    private async Task SeedCategoriesAsync(PmmDbContext ctx, CancellationToken ct)
    {
        if (await ctx.CategoryDefinitions.AnyAsync(ct))
        {
            _logger.LogDebug("分类表已有数据，跳过分类种子补齐");
            return;
        }

        CategoryDefinition movie = new() { Name = "电影", MediaType = MediaType.Movie, TargetRoot = UnsetRoot, Description = "默认电影分类，请在「设置-分类」改为实际目标根目录" };
        CategoryDefinition tv = new() { Name = "电视剧", MediaType = MediaType.Tv, TargetRoot = UnsetRoot, Description = "真人电视剧（不限地域），请在「设置-分类」改为实际目标根目录" };
        CategoryDefinition anime = new() { Name = "动漫", MediaType = MediaType.Tv, TargetRoot = UnsetRoot, Description = "动画剧集（含日本动画与各国动画）" };

        ctx.CategoryDefinitions.AddRange(movie, tv, anime);
        await ctx.SaveChangesAsync(ct);

        // 匹配规则按 Priority 升序「先命中先采用」：先判最具体的动漫（动画类剧集），其余剧集走电视剧兜底，最后电影兜底。
        // genres 用 in + 中英文双值，兼容 TMDB 语言设置切换（zh-CN「动画」↔ en-US「Animation」）。
        ctx.CategoryMatchRules.AddRange(
            new CategoryMatchRule
            {
                CategoryId = anime.Id,
                Name = "动漫：动画类剧集",
                Conditions = """{"all":[{"field":"type","op":"eq","value":"tv"},{"field":"genres","op":"in","value":["动画","Animation"]}]}""",
                Priority = 20,
            },
            new CategoryMatchRule
            {
                CategoryId = tv.Id,
                Name = "电视剧：所有剧集兜底",
                Conditions = """{"field":"type","op":"eq","value":"tv"}""",
                Priority = 50,
            },
            new CategoryMatchRule
            {
                CategoryId = movie.Id,
                Name = "电影：所有电影兜底",
                Conditions = """{"field":"type","op":"eq","value":"movie"}""",
                Priority = 90,
            });
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation("已补齐 3 个默认分类与 3 条分类匹配规则");
    }

    /// <summary>补齐内置正则引擎覆盖不到的解析规则；解析规则表非空则跳过</summary>
    private async Task SeedParseRulesAsync(PmmDbContext ctx, CancellationToken ct)
    {
        if (await ctx.ParseRules.AnyAsync(ct))
        {
            _logger.LogDebug("解析规则表已有数据，跳过解析规则种子补齐");
            return;
        }

        // 全部规则均带强前置特征（必须有特定标记或定位锚），只命中目标命名，
        // 不会劫持普通文件名；命中失败则照常回落到内置正则引擎。
        // Priority 间隔 5–10 是为了用户后续插入自定义规则时留余地。
        ctx.ParseRules.AddRange(
            new ParseRule
            {
                Name = "国产剧第N季第N集",
                Scope = ParseScope.FileName,
                // 形如：琅琊榜.第1季.第03集.1080p.mkv / 庆余年 第2季 第10集.mkv
                // 内置引擎可单独识别「第N季」「第N集」，但拼装时标题易夹杂分隔符；显式规则一次抓干净
                Pattern = @"^(?<title>.+?)[\s\._\-]*第(?<season>\d{1,2})季[\s\._\-]*第(?<episode>\d{1,4})[集话話]",
                DefaultType = "tv",
                Priority = 25,
                ConfidenceBonus = 0.05,
                Description = "国产/华语剧「第N季第N集」组合命名（如「琅琊榜.第1季.第03集」）",
            },
            new ParseRule
            {
                Name = "Plex Jellyfin 标准命名",
                Scope = ParseScope.FileName,
                // 形如：Breaking Bad - S01E01 - Pilot.mkv —— Plex/Jellyfin 推荐命名，` - ` 包围 SxxExx
                Pattern = @"^(?<title>.+?)\s+-\s+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})\s+-\s+",
                DefaultType = "tv",
                Priority = 30,
                ConfidenceBonus = 0.05,
                Description = "Plex / Jellyfin 推荐格式「Show - SxxExx - Title」，标题与季集分隔最干净",
            },
            new ParseRule
            {
                Name = "括号年份带季集",
                Scope = ParseScope.FileName,
                // 形如：Game of Thrones (2011) S01E01.mkv —— 一次抓全 title/year/season/episode
                Pattern = @"^(?<title>.+?)\s*\((?<year>\d{4})\)[\s\.\-_]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})",
                DefaultType = "tv",
                Priority = 35,
                ConfidenceBonus = 0.05,
                Description = "「Show Name (2020) S01E01」组合命名，一次抓 title/year/season/episode",
            },
            new ParseRule
            {
                Name = "方括号包裹剧集",
                Scope = ParseScope.FileName,
                // 形如：[字幕组][番名][01][1080p][BIG5].mkv —— 标题与集号都在独立方括号里
                // 内置规则对此种纯方括号链的标题抓取无能为力
                Pattern = @"^(?:\[[^\]]{1,40}\]\s*)*\[(?<title>[^\[\]]{1,40})\]\s*\[(?<episode>\d{1,4})(?:v\d)?\]",
                DefaultType = "tv",
                Priority = 45,
                ConfidenceBonus = 0.05,
                Description = "纯方括号链命名「[字幕组][剧名][01][1080p]」，国漫/番剧常见",
            },
            new ParseRule
            {
                Name = "动漫字幕组单集",
                Scope = ParseScope.FileName,
                // 形如：[字幕组] 番名 - 12 [WebRip 1080p].mkv —— 内置引擎对无 E/EP 前缀的纯集号无能为力
                Pattern = @"^(?:\[[^\]]{1,40}\]\s*)+(?<title>[^\[\]]+?)\s*-\s*(?<episode>\d{1,4})(?:v\d)?\s*(?:\[|\.|$)",
                DefaultType = "tv",
                Priority = 50,
                ConfidenceBonus = 0.05,
                Description = "匹配方括号字幕组前缀 + 「标题 - 集号」的动漫单集命名",
            },
            new ParseRule
            {
                Name = "OVA SP 特别篇",
                Scope = ParseScope.FileName,
                // 形如：[字幕组] 进击的巨人 OVA1 [BDRip].mkv / 鬼灭之刃 SP01.mkv
                // 必须强制有分隔符前导避免吞掉 Spectre 这类含 SP 的英文词；尾部 (?![\d]) 防年份吃半截
                // 特别篇约定 season=0（媒体库 Specials 季）：ParseRule 无「常量季号」字段、正则无法凭空捕获 0，
                // 故季号留空 →「tv 缺季」置信度 0.50 → AI 兜底（AiPromptHelpers.SystemPrompt 已约定特别篇 season 输出 0）。
                Pattern = OvaPattern,
                DefaultType = "tv",
                Priority = 55,
                Description = OvaDescription,
            },
            new ParseRule
            {
                Name = "季集 NxNN 格式",
                Scope = ParseScope.FileName,
                // 形如：Show.Name.1x02.720p.mkv —— 内置引擎只认 S01E02，不认 1x02
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?<season>\d{1,2})x(?<episode>\d{1,3})(?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 60,
                Description = "匹配「季x集」（如 1x02）写法的剧集命名",
            },
            new ParseRule
            {
                Name = "括号年份电影",
                Scope = ParseScope.FileName,
                // 形如：Movie Title (2020) 1080p.mkv —— 内置规则会把 title 截到 `Movie Title (` 留下半截括号
                // DefaultType 留空让 InferMediaType 兜底（含 season/eps 不为 null 自动判 tv；纯 year 判 movie）
                Pattern = @"^(?<title>.+?)\s*\((?<year>\d{4})\)",
                DefaultType = null,
                Priority = 70,
                Description = "「电影名 (2020) ...」括号年份命名，标题剥得比内置规则干净",
            },
            new ParseRule
            {
                Name = "综艺第N季 + 日期作集",
                Scope = ParseScope.FileName,
                // 形如：极限挑战.第7季.20210501.1080p.mkv / Running.Man.第N季.20230101.mkv
                // year + episode(MMDD)：episode 用 4 位月日，便于排序 0501 < 0815；同时年份独立抓出
                // 优先级高于「综艺日期作集」基础版（要先尝试匹配带季号变体）
                Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<season>\d{1,2})季[\s\._\-]+(?<year>20\d{2})(?<episode>\d{4})(?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 22,
                ConfidenceBonus = 0.05,
                Description = "综艺命名「节目名 第N季 YYYYMMDD」：season 抓季号，year 抓年份，episode 抓 MMDD（按时间顺序排序）",
            },
            new ParseRule
            {
                Name = "综艺日期作集",
                Scope = ParseScope.FileName,
                // 形如：极限挑战.20210501.1080p.mkv / 奔跑吧兄弟.20230101.mkv / Running.Man.20230101.HDTV.mkv
                // 综艺真人秀按播出日期作集是行业惯例；TMDB 端有的会按 air_date 反查 episode
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?<year>20\d{2})(?<episode>\d{4})(?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 28,
                ConfidenceBonus = 0.05,
                Description = "综艺 / 真人秀「节目名 YYYYMMDD」：year 抓年份，episode 抓 MMDD（按时间顺序排序）",
            },
            new ParseRule
            {
                Name = "AKA 多语言标题",
                Scope = ParseScope.FileName,
                // 形如：Vagabond.AKA.바가본드.S01E01.mkv / 钢炼.AKA.Fullmetal.Alchemist.S01E01.mkv
                // 用 AKA / 也叫 / a.k.a. 分隔多语言标题，主标题取 AKA 前段（避免取到外语写法）
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?:AKA|aka|a\.k\.a\.|也叫)[\s\._\-]+.+?[\s\._\-]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})",
                DefaultType = "tv",
                Priority = 32,
                ConfidenceBonus = 0.05,
                Description = "AKA 多语言标题命名「主标题 AKA 别名 SxxEyy」，取 AKA 前的主标题",
            },
            new ParseRule
            {
                Name = "Anime 英文季号 (Nth Season)",
                Scope = ParseScope.FileName,
                // 形如：Show 2nd Season E01.mkv / Some Show.3rd.Season.E12.mkv / Title 4th Season EP05.mkv
                // 番剧英文命名常见，1st/2nd/3rd/4th-99th Season + 集号；season 单独从 1st/2nd 数字部分抓
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?<season>\d{1,2})(?:st|nd|rd|th)[\s\._\-]?Season[\s\._\-]+(?:E|EP|Episode)?[\s\._\-]?(?<episode>\d{1,4})",
                DefaultType = "tv",
                Priority = 48,
                ConfidenceBonus = 0.05,
                Description = "番剧英文季号「Show 2nd Season E01」，捕获 season 数字（1st/2nd/3rd/Nth）+ episode",
            },
            new ParseRule
            {
                Name = "全N集 整季合集",
                Scope = ParseScope.FileName,
                // 形如：扫毒.全30集.HD.mkv / 庆余年 全46集 1080p.mkv
                // 整季打包通常是单文件「全部集合并」：识别为 tv 并抓干净标题，但「全N集」的 N 是总集数不是集号，
                // 不能捕获为 episode（否则「全30集」归档成 S01E30 误指向第 30 集）；
                // 季/集均留空 →「tv 缺季集」置信度压到 0.50，交 AI / 人工审核定季集。
                // 存量库中仍保留旧版 Pattern（捕获总集数）的种子行由 FixLegacyParseRulesAsync 启动时自动修正，
                // 用户改过的行不受影响。
                Pattern = FullPackPattern,
                DefaultType = "tv",
                ForceType = true,
                Priority = 65,
                ConfidenceBonus = 0.0,
                Description = FullPackDescription,
            },
            new ParseRule
            {
                Name = "Episode N 完整英文",
                Scope = ParseScope.FileName,
                // 形如：Show.Episode.12.1080p.mkv / Some Show Episode 5.mkv
                // 内置 EpisodeOnly 只匹配 EP/E + 数字，不匹配完整的 Episode；显式规则补齐
                Pattern = @"^(?<title>.+?)[\s\._\-]+Episode[\s\._\-]?(?<episode>\d{1,4})(?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 75,
                ConfidenceBonus = 0.0,
                Description = "「Show Episode 12」完整英文集号命名，补内置 EP/E 简写规则未覆盖的全词形式",
            },
            new ParseRule
            {
                Name = "中文章节集号「第N章 / 第N回」",
                Scope = ParseScope.FileName,
                // 形如：三体.第1章.mkv / 天龙八部.第10回.HDTV.mkv
                // 内置 EpisodeChinese 仅匹配「集/话/話」，「章/回」是漫画化番剧 / 网络剧 / 评书常见
                Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})[章回段]",
                DefaultType = "tv",
                Priority = 80,
                ConfidenceBonus = 0.0,
                Description = "中文「第N章 / 第N回 / 第N段」章节集号，覆盖漫画化番剧 / 网络剧 / 评书命名",
            },
            new ParseRule
            {
                Name = "中文「第N部 第N集」",
                Scope = ParseScope.FileName,
                // 形如：三国演义.第1部.第10集.mkv / 我的天才女友 第2部 第12集.mkv
                // 「部」与「季」语义平行，国剧 / 系列剧 / 翻拍剧常用「部」作为分季单位
                Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<season>\d{1,2})部[\s\._\-]+第(?<episode>\d{1,4})[集话話]",
                DefaultType = "tv",
                Priority = 26,
                ConfidenceBonus = 0.05,
                Description = "国剧 / 系列剧「第N部 第N集」组合命名（如「我的天才女友 第2部 第12集」），「部」作为分季单位",
            },
            new ParseRule
            {
                Name = "方括号双段季集 [Sxx][Eyy]",
                Scope = ParseScope.FileName,
                // 形如：[字幕组][番名][S01][E12][1080p].mkv / [番名][S02][EP08].mkv
                // 与「方括号包裹剧集」不同：本规则要求季 + 集独立各占一对方括号且明确 S/E 标记
                // 不抓 title：前置方括号串混杂字幕组 / 标题 / 语言，让 CleanedStem 兜底剥噪声后取标题
                Pattern = @"\[[Ss](?<season>\d{1,2})\]\s*\[[Ee][Pp]?(?<episode>\d{1,4})\]",
                DefaultType = "tv",
                Priority = 27,
                ConfidenceBonus = 0.05,
                Description = "字幕组双方括号格式「[番名][S01][E12]」，季集各自独立方括号且带 S/E 前缀",
            },
            new ParseRule
            {
                Name = "综艺「第N期」",
                Scope = ParseScope.FileName,
                // 形如：周日真好.第300期.HDTV.mkv / Knowing Bros 第300期.mkv
                // 韩综 / 日综常用「期」作为集号单位，与「集 / 话 / 話 / 章 / 回 / 段」语义平行
                Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})期",
                DefaultType = "tv",
                Priority = 29,
                ConfidenceBonus = 0.05,
                Description = "韩综 / 日综 / 真人秀「第N期」集号命名（如 Knowing Bros 第300期），「期」作为集号单位",
            },
            new ParseRule
            {
                Name = "番剧分卷 Part / Cour + 集号",
                Scope = ParseScope.FileName,
                // 形如：Mob Psycho 100 Part 2 E03 / Kaguya-sama 2nd Cour E05 / Show Name Cour 2 E12
                // Part / Cour 是番剧分卷概念，独立于真季号；规则只抓 episode，让 season 走 TMDB 反查
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?:(?:Part|Cour)[\s\._\-]?\d{1,2}|\d(?:st|nd|rd|th)[\s\._\-]?Cour)[\s\._\-]+(?:E|EP|Episode)[\s\._\-]?(?<episode>\d{1,4})",
                DefaultType = "tv",
                Priority = 49,
                ConfidenceBonus = 0.05,
                Description = "番剧分卷标记「Show Part 2 E03」「Show 2nd Cour E05」，title + episode（season 留空交 TMDB 反查）",
            },
            new ParseRule
            {
                Name = "动漫 Vol / Volume 卷集号",
                Scope = ParseScope.FileName,
                // 形如：[VCB-Studio] 进击的巨人 Vol.01 [BDRip].mkv / Some Show Volume 3 BDBox.mkv
                // BD/BDBox 命名常用 Vol 标记，集号通常 1-3 位（一卷 ≤ 几集）
                Pattern = @"^(?:\[[^\]]{1,40}\]\s*)*(?<title>[^\[\]]+?)[\s\._\-]+Vol(?:ume)?\.?[\s\._\-]?(?<episode>\d{1,3})(?:[\s\._\-\[]|$)",
                DefaultType = "tv",
                Priority = 52,
                ConfidenceBonus = 0.0,
                Description = "动漫 BD/BDBox 卷标记「Vol.01」「Volume 3」，集号 1-3 位（一卷代表数集）",
            },
            new ParseRule
            {
                Name = "绝对集号「#N / No.N」",
                Scope = ParseScope.FileName,
                // 形如：One Piece #1000.mkv / 海贼王 No.1100.mkv / 名侦探柯南 #1234.mkv
                // 长篇番剧（海贼王 / 名侦探柯南 / 火影）无季号、用绝对集号标记，#或No.前缀
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?:#|No\.)[\s\._]?(?<episode>\d{1,4})(?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 82,
                ConfidenceBonus = 0.0,
                Description = "长篇番剧绝对集号「Show #N」「Show No.N」（如 One Piece #1000），无季号 + 大集号",
            },
            new ParseRule
            {
                Name = "无季号「第N集」中文兜底",
                Scope = ParseScope.FileName,
                // 形如：鬼灭之刃.第3集.1080p.mkv / 庆余年 第10话.mkv
                // 内置 EpisodeChinese 能抓 episode，但 title 易夹杂分隔符 / 噪声；显式兜底一次抓全
                // priority 83：低于「国产剧第N季第N集」（25）和「综艺第N季 + 日期作集」（22），不抢更具体规则
                Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})[集话話](?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 83,
                ConfidenceBonus = 0.0,
                Description = "无季号场景下纯中文「第N集 / 第N话 / 第N話」单集兜底，title 抓得比内置规则干净",
            },
            new ParseRule
            {
                Name = "综艺 YYMMDD 短日期作集",
                Scope = ParseScope.FileName,
                // 形如：Running.Man.210501.HDTV.mkv / Knowing.Bros.231215.mkv
                // 韩综 / 日综常用 6 位短日期（年份取后 2 位）；严格限 [12] 开头避免误吞其它 6 位数字
                // episode 存 YYMMDD 整数，便于排序（210501 < 231215）
                Pattern = @"^(?<title>.+?)[\s\._\-]+(?<episode>[12]\d{5})(?:[\s\._\-]|$)",
                DefaultType = "tv",
                Priority = 85,
                ConfidenceBonus = 0.0,
                Description = "韩综 / 日综短日期「节目名 YYMMDD」，6 位日期（10-29 年范围）作 episode 排序",
            });
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation("已补齐 23 条默认解析规则");
    }

    /// <summary>存量种子规则一次性修正：仍保留旧种子原值的行自动替换为新值</summary>
    /// <remarks>
    /// 种子幂等策略是「整表为空才写入」（见类注释），历史库已落库的旧版种子行不会随种子文本修正而更新；
    /// 本方法补上这一缺口：仅当行字段**精确等于旧种子值**（说明用户从未改过）才替换为新值，
    /// 用户改过的行（值已不等于旧种子值）绝不触碰。每次启动都执行但天然幂等——旧值匹配不到即无事发生；
    /// 新库种子写入的就是新值，本方法空转。
    /// 当前覆盖两处（2026-06 解析规则组修复 418519d 引入的种子文本变更）：
    ///   1.「全N集 整季合集」：旧 Pattern 把总集数捕获为 episode（整季包被归档成第 N 集）→ 换为不抓集号的新 Pattern；
    ///     Description 仅在同为旧种子原文时一并刷新，用户自定义描述保留。
    ///   2.「OVA SP 特别篇」：Pattern 未变，仅 Description 补充「季号留空交 AI 归 Season 0」说明；
    ///     要求 Pattern 与 Description 均为种子原值才刷新，避免误改用户行。
    /// </remarks>
    private async Task FixLegacyParseRulesAsync(PmmDbContext ctx, CancellationToken ct)
    {
        int updated = 0;

        // 1.「全N集 整季合集」旧 Pattern → 新 Pattern（功能缺陷修正，按 Pattern 精确等于旧种子值判定）
        List<ParseRule> legacyFullPack = await ctx.ParseRules
            .Where(r => r.Pattern == LegacyFullPackPattern)
            .ToListAsync(ct);
        foreach (ParseRule rule in legacyFullPack)
        {
            rule.Pattern = FullPackPattern;
            if (rule.Description == LegacyFullPackDescription)
            {
                rule.Description = FullPackDescription;
            }
            updated++;
            _logger.LogInformation(
                "存量种子规则修正：「{Name}」(Id={Id}) Pattern 更新为不捕获总集数的新种子值（旧值会把整季包归档成第 N 集）",
                rule.Name, rule.Id);
        }

        // 2.「OVA SP 特别篇」旧 Description → 新 Description（仅说明文本，Pattern 与 Description 须均为种子原值）
        List<ParseRule> legacyOva = await ctx.ParseRules
            .Where(r => r.Pattern == OvaPattern && r.Description == LegacyOvaDescription)
            .ToListAsync(ct);
        foreach (ParseRule rule in legacyOva)
        {
            rule.Description = OvaDescription;
            updated++;
            _logger.LogInformation(
                "存量种子规则修正：「{Name}」(Id={Id}) Description 补充特别篇 Season 0 约定说明",
                rule.Name, rule.Id);
        }

        if (updated > 0)
        {
            await ctx.SaveChangesAsync(ct);
            _logger.LogInformation("存量种子解析规则一次性修正完成：共更新 {Count} 条", updated);
        }
    }
}
