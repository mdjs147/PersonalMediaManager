using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;
using Setup = PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>RuleEngineService（D7.2）— 内置规则 + 用户规则 + 置信度评分</summary>
public sealed class RuleEngineServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly RuleEngineService _sut;

    public RuleEngineServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using PmmDbContext ctx = _dbFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _sut = new RuleEngineService(_dbFactory, NullLogger<RuleEngineService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>测试 helper：把旧 (fileName, parentFolderName) 入参翻译为 FileParseContext 调用</summary>
    /// <remarks>
    /// 测试用的「监控根」固定为 C:\watch（跨平台值无所谓，FileParseContext.FromFullPath 内部用
    /// Path.GetRelativePath 算出 RelativeSegments，与盘符无关）。
    /// parentFolderName=null → FileNameOnly 模式，RelativeSegments=[]，等价于旧的「无父目录」。
    /// </remarks>
    private Task<RuleParseResult> Parse(string fileName, string? parentFolderName, CancellationToken ct = default)
    {
        FileParseContext ctx = parentFolderName is null
            ? FileParseContext.FileNameOnly(fileName)
            : FileParseContext.FromFullPath(
                Path.Combine("C:\\watch", parentFolderName, fileName),
                "C:\\watch");
        return _sut.ParseAsync(ctx, ct);
    }

    // ---------- 内置规则：电影 ----------

    [Fact]
    public async Task Builtin_Movie_TitleYearOnly_Confidence085()
    {
        RuleParseResult r = await Parse("Inception.2010.1080p.BluRay.x264.mkv", parentFolderName: null);

        r.Title.Should().Be("Inception");
        r.Year.Should().Be(2010);
        r.MediaType.Should().Be("movie");
        r.Season.Should().BeNull();
        r.Episode.Should().BeNull();
        r.Confidence.Should().Be(0.85);
        r.MatchedRuleId.Should().BeNull();
    }

    [Fact]
    public async Task Builtin_NoiseTokens_Stripped_From_Title()
    {
        RuleParseResult r = await Parse("Some.Movie.2018.2160p.UHD.BluRay.HEVC.HDR10.Atmos-GROUP.mkv", parentFolderName: null);

        r.Title.Should().Be("Some Movie");
        r.Year.Should().Be(2018);
        r.MediaType.Should().Be("movie");
    }

    [Fact]
    public async Task Builtin_BracketReleaseGroup_Removed()
    {
        RuleParseResult r = await Parse("[ReleaseGroup] My Movie 2020 1080p.mkv", parentFolderName: null);

        r.Title.Should().Be("My Movie");
        r.Year.Should().Be(2020);
    }

    // ---------- 内置规则：剧集 ----------

    [Theory]
    [InlineData("BreakingBad.S01E02.1080p.mkv", 1, 2)]
    [InlineData("Some.Show.s5e15.HDTV.mkv", 5, 15)]
    [InlineData("Foo.S01.E02.x264.mkv", 1, 2)]
    public async Task Builtin_Tv_SeasonEpisodeLatin(string fileName, int expectedSeason, int expectedEpisode)
    {
        RuleParseResult r = await Parse(fileName, parentFolderName: null);

        r.MediaType.Should().Be("tv");
        r.Season.Should().Be(expectedSeason);
        r.Episode.Should().Be(expectedEpisode);
    }

    [Fact]
    public async Task Builtin_Tv_ChineseEpisode()
    {
        RuleParseResult r = await Parse("绝命毒师 第03集 1080p.mkv", parentFolderName: null);

        r.Episode.Should().Be(3);
        r.MediaType.Should().Be("tv");
        r.Title.Should().Contain("绝命毒师");
    }

    [Fact]
    public async Task Builtin_Tv_ParentFolderProvides_Season()
    {
        RuleParseResult r = await Parse("EP01.mkv", parentFolderName: "绝命毒师 第1季 1080p");

        r.Season.Should().Be(1);
        r.Episode.Should().Be(1);
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task Builtin_BracketEpisode()
    {
        RuleParseResult r = await Parse("[字幕组] 某番 [01][1080p].mkv", parentFolderName: null);

        r.Episode.Should().Be(1);
        r.MediaType.Should().Be("tv");
        // 剧集仅缺季号（集号在）→ 0.70 走 TMDB 直查：下游「单季自动补季」自动定 S01、
        // 多季剧转人工审核选季，归档前必被补全，不会走到 ArchiveService 抛 BusinessException
        r.Confidence.Should().Be(0.70);
    }

    [Fact]
    public async Task Builtin_Tv_AllBracketName_NoValidTitle_Confidence050()
    {
        // 复现真实日志样本：[字幕组][剧名][绝对集数][技术参数] 全方括号命名，整条路径无季号标记。
        // 内置规则的 GroupBracket 会把方括号块全部剥光 → 无有效标题（生产环境由种子规则 P45
        // 「方括号包裹剧集」捕获 title 走直查）→ 标题无效压 0.50 走 AI 兜底，不能拿原始串直查 TMDB。
        FileParseContext ctx = FileParseContext.FromFullPath(
            "F:\\迅雷下载\\[BeanSub&FZSD][Jujutsu_Kaisen][48-59][GB][1080P][MP4]\\[BeanSub&FZSD][Jujutsu_Kaisen][59][GB][1080P][x264_AAC].mp4",
            "F:\\迅雷下载");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.MediaType.Should().Be("tv");
        r.Episode.Should().Be(59);
        r.Season.Should().BeNull("整条路径无任何季号标记");
        r.Confidence.Should().Be(0.50, "内置规则对全方括号命名提不出有效标题，压 0.50 走 AI（种子规则 P45 命中时才 0.75 直查）");
    }

    [Fact]
    public async Task Builtin_AllFieldsHit_Confidence095()
    {
        RuleParseResult r = await Parse("Breaking.Bad.S01E02.2008.1080p.BluRay.mkv", parentFolderName: null);

        r.MediaType.Should().Be("tv");
        r.Season.Should().Be(1);
        r.Episode.Should().Be(2);
        r.Year.Should().Be(2008);
        r.Confidence.Should().Be(0.95);
    }

    // ---------- 类型推断 ----------

    [Fact]
    public async Task TypeInference_ParentFolder_TV_Hint()
    {
        RuleParseResult r = await Parse("misc.mkv", parentFolderName: "Anime");
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task TypeInference_ParentFolder_Movie_Hint()
    {
        RuleParseResult r = await Parse("misc.mkv", parentFolderName: "电影合集");
        r.MediaType.Should().Be("movie");
    }

    [Fact]
    public async Task TypeInference_NoContextNoEpisode_Unknown()
    {
        RuleParseResult r = await Parse("RandomGarbage.mkv", parentFolderName: null);
        r.MediaType.Should().Be("unknown");
        r.Confidence.Should().Be(0.50); // 仅 title
    }

    // ---------- 特殊字符 ----------

    [Fact]
    public async Task SpecialChars_CJK_Latin_Mix_Sets_HasSpecialChars_True()
    {
        RuleParseResult r = await Parse("君の名は Your Name 2016.mkv", parentFolderName: null);
        r.HasSpecialChars.Should().BeTrue();
    }

    [Fact]
    public async Task SpecialChars_PureCJK_OrPureLatin_NotSpecial()
    {
        RuleParseResult a = await Parse("Inception.2010.mkv", parentFolderName: null);
        RuleParseResult b = await Parse("绝命毒师 第03集.mkv", parentFolderName: null);
        a.HasSpecialChars.Should().BeFalse();
        b.HasSpecialChars.Should().BeFalse();
    }

    // ---------- 用户规则 ----------

    [Fact]
    public async Task UserRule_Hit_Returns_MatchedRuleId_AndBonus()
    {
        long ruleId = SeedRule(new ParseRule
        {
            Name = "custom",
            Enabled = true,
            Priority = 10,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>[A-Za-z]+)\.(?<year>\d{4})",
            DefaultType = "movie",
            ForceType = true,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("CustomMovie.2022.1080p.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().Be(ruleId);
        r.Title.Should().Be("CustomMovie");
        r.Year.Should().Be(2022);
        r.MediaType.Should().Be("movie");
        // 基础 0.85（title+type+year+movie）+ bonus 0.05 = 0.90
        r.Confidence.Should().BeApproximately(0.90, 0.001);
    }

    [Fact]
    public async Task UserRule_Priority_LowerFirst_Wins()
    {
        SeedRule(new ParseRule
        {
            Name = "specific",
            Enabled = true,
            Priority = 1,
            Scope = ParseScope.FileName,
            Pattern = @"^Foo\.(?<year>\d{4})",
            DefaultType = "movie", ForceType = true,
            ConfidenceBonus = 0,
        });
        SeedRule(new ParseRule
        {
            Name = "catchall",
            Enabled = true,
            Priority = 100,
            Scope = ParseScope.FileName,
            Pattern = @".+",
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0,
        });

        RuleParseResult r = await Parse("Foo.2010.1080p.mkv", parentFolderName: null);
        r.MediaType.Should().Be("movie");
        r.Year.Should().Be(2010);
    }

    /// <summary>种子「动漫字幕组单集」Pattern（与 DataSeeder 一致），用户规则小数集守护用例共用</summary>
    private const string FansubEpisodePattern =
        @"^(?:\[[^\]]{1,40}\]\s*)+(?<title>[^\[\]]+?)\s*-\s*(?<episode>\d{1,4})(?:v\d)?\s*(?:\[|\.|$)";

    [Fact]
    public async Task UserRule_FractionalEpisodeTail_DropsEpisode()
    {
        // 「番名 - 11.5 [..]」：episode 组截到整数部分 11、组末尾紧跟 .5 → 结果层守护丢弃集号转低置信
        SeedRule(new ParseRule
        {
            Name = "动漫字幕组单集",
            Enabled = true,
            Priority = 10,
            Scope = ParseScope.FileName,
            Pattern = FansubEpisodePattern,
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0,
        });

        RuleParseResult r = await Parse("[喵萌奶茶屋] 某番剧 - 11.5 [WebRip 1080p].mkv", parentFolderName: null);

        r.Episode.Should().BeNull("总集篇 11.5 不得截成第 11 集顶替正片");
        r.EpisodeEnd.Should().BeNull();
        r.Confidence.Should().BeLessThan(0.6, "缺集号应压到走 AI / 人工审核");
    }

    [Theory]
    [InlineData("[喵萌奶茶屋] 某番剧 - 11 [WebRip 1080p].mkv", 11)]   // 整数集不受影响
    [InlineData("[喵萌奶茶屋] 某番剧 - 11v2 [1080p].mkv", 11)]        // v2 修正版照常
    public async Task UserRule_IntegerEpisode_Unaffected(string fileName, int expected)
    {
        SeedRule(new ParseRule
        {
            Name = "动漫字幕组单集",
            Enabled = true,
            Priority = 10,
            Scope = ParseScope.FileName,
            Pattern = FansubEpisodePattern,
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0,
        });

        RuleParseResult r = await Parse(fileName, parentFolderName: null);

        r.Episode.Should().Be(expected);
    }

    [Fact]
    public async Task UserRule_FractionalEpisodeEndTail_DropsBoth()
    {
        // 范围末端带小数（08-09.5）：episodeEnd 组末尾紧跟 .5 → 守护同样生效，双双丢弃
        SeedRule(new ParseRule
        {
            Name = "范围集",
            Enabled = true,
            Priority = 10,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)\s+(?<episode>\d{1,4})-(?<episodeEnd>\d{1,4})",
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0,
        });

        RuleParseResult r = await Parse("某番剧 08-09.5 [1080p].mkv", parentFolderName: null);

        r.Episode.Should().BeNull();
        r.EpisodeEnd.Should().BeNull();
    }

    [Fact]
    public async Task UserRule_Disabled_NotMatched_FallsTo_Builtin()
    {
        SeedRule(new ParseRule
        {
            Name = "disabled",
            Enabled = false,
            Priority = 1,
            Scope = ParseScope.FileName,
            Pattern = @"^.+",
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0.3,
        });

        RuleParseResult r = await Parse("Inception.2010.1080p.mkv", parentFolderName: null);
        r.MatchedRuleId.Should().BeNull();
        r.MediaType.Should().Be("movie");
    }

    [Fact]
    public async Task UserRule_InvalidRegex_SkippedNotThrown()
    {
        SeedRule(new ParseRule
        {
            Name = "bad-regex",
            Enabled = true,
            Priority = 1,
            Scope = ParseScope.FileName,
            Pattern = @"[unclosed-bracket",
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0,
        });
        SeedRule(new ParseRule
        {
            Name = "good",
            Enabled = true,
            Priority = 2,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)\.",
            DefaultType = "movie", ForceType = true,
            ConfidenceBonus = 0,
        });

        Func<Task> act = async () => await Parse("Foo.bar.mkv", parentFolderName: null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UserRule_AnimeBracketChain_TitleSeparatorsCollapsed()
    {
        // 种子默认含「方括号包裹剧集」规则；此处显式 seed 同 Pattern 模拟数据库就绪状态，
        // 验证：六方括号链命名 [字幕组][标题_含下划线][集号][语言][分辨率][编码] 能正确解析为
        // title=「Jujutsu Kaisen」（下划线已折叠为空格）、episode=59
        SeedRule(new ParseRule
        {
            Name = "方括号包裹剧集",
            Enabled = true,
            Priority = 45,
            Scope = ParseScope.FileName,
            Pattern = @"^(?:\[[^\]]{1,40}\]\s*)*\[(?<title>[^\[\]]{1,40})\]\s*\[(?<episode>\d{1,4})(?:v\d)?\]",
            DefaultType = "tv",
            ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse(
            "[BeanSub&FZSD][Jujutsu_Kaisen][59][GB][1080P][x264_AAC].mp4",
            parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Jujutsu Kaisen");
        r.Episode.Should().Be(59);
        r.Season.Should().BeNull();
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task UserRule_Scope_ParentFolder_MatchesOnParent()
    {
        SeedRule(new ParseRule
        {
            Name = "parent-only",
            Enabled = true,
            Priority = 1,
            Scope = ParseScope.ParentFolder,
            Pattern = @"^(?<title>[A-Z][a-z]+)",
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0,
        });

        RuleParseResult r = await Parse("ep01.mkv", parentFolderName: "Breaking Bad 第一季");

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Breaking");
    }

    // ---------- 中文数字 / 罗马数字 / 篇章 季号（本次新增）----------

    [Theory]
    [InlineData("庆余年 第二季", 2)]
    [InlineData("某剧 第十季", 10)]
    [InlineData("某剧 第二十一季", 21)]
    public async Task Builtin_ChineseNumeralSeason_Parsed(string parentFolder, int expectedSeason)
    {
        // 「第二季」中文数字季号必须识别（间谍过家家落待确认队列的真正根因）
        RuleParseResult r = await Parse("第01集.mkv", parentFolderName: parentFolder);
        r.Season.Should().Be(expectedSeason);
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task Builtin_RomanNumeralSeason_TailParsed_TitleStripped()
    {
        // 罗马数字季号（动漫）：刀剑神域 II → season=2，标题剥到罗马数字前
        RuleParseResult r = await Parse("刀剑神域 II [01].mkv", parentFolderName: null);
        r.Season.Should().Be(2);
        r.Title.Should().Be("刀剑神域");
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task Builtin_RomanNumeral_NotMisfired_By_LowercaseCodec()
    {
        // x264 的小写 x 不得被当罗马数字 X（NoIgnoreCase 守护，否则电影被误判成剧集第 10 季）
        RuleParseResult r = await Parse("Inception.2010.1080p.BluRay.x264.mkv", parentFolderName: null);
        r.MediaType.Should().Be("movie");
        r.Season.Should().BeNull();
    }

    [Fact]
    public async Task Builtin_SeasonArcTitle_Extracted_TitleStripped()
    {
        // 篇章季标题：鬼灭之刃 锻刀村篇 → seasonTitle=锻刀村篇，主标题剥到篇章前，供人工对照 TMDB 季名
        RuleParseResult r = await Parse("鬼灭之刃 锻刀村篇 第01集.mkv", parentFolderName: null);
        r.SeasonTitle.Should().Be("锻刀村篇");
        r.Title.Should().Be("鬼灭之刃");
    }

    // ---------- HasMixedCjkLatin 边界 ----------

    [Theory]
    [InlineData("你好 World 测试 Hello", true)]
    [InlineData("PureEnglishOnly", false)]
    [InlineData("纯中文标题", false)]
    [InlineData("ABC 你", false)] // CJK<3
    [InlineData("中文标题加 A 后缀", false)] // latin<3
    public void HasMixedCjkLatin_Boundary(string s, bool expected)
    {
        RuleEngineService.HasMixedCjkLatin(s).Should().Be(expected);
    }

    // ---------- 真实场景：多层目录承载剧名（样本 1）----------

    [Fact]
    public async Task RealSample_MultiLayerDirs_GrandparentCarriesTitle()
    {
        // F:\迅雷下载\国务卿女士 6季\第1季\01.mp4
        // 监控根 = F:\迅雷下载，文件名 01.mp4 只能看出第 1 集，第1季 给出季号，
        // **国务卿女士 6季** 才是剧名。当前内置规则应能从祖父目录回填 title
        FileParseContext ctx = FileParseContext.FromFullPath(
            "F:\\迅雷下载\\国务卿女士 6季\\第1季\\01.mp4",
            "F:\\迅雷下载");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Episode.Should().Be(1, "文件名 01 应识别为集号");
        r.Season.Should().Be(1, "父目录「第1季」应识别为季号");
        r.MediaType.Should().Be("tv");
        // 标题回退到祖父目录，且「6季」总季数后缀必须被清洗掉（精确匹配，不能残留「6季」）
        r.Title.Should().Be("国务卿女士", "祖父目录作为标题候选，且总季数后缀「6季」应被剥离");
    }

    // ---------- 真实场景：单层超长目录承载全部元信息（样本 2）----------

    [Fact]
    public async Task RealSample_PtSinglePackedDir_ExtractsAllFields()
    {
        // 父目录承载全部元信息，文件本身只剩 SxxExx 双集合并
        FileParseContext ctx = FileParseContext.FromFullPath(
            "F:\\迅雷下载\\【高清剧集网发布 www.PTHDTV.com】低智商犯罪[第08-09集][国语音轨+简繁英字幕].Born.with.Luck.S01.2026.2160p.IQ.WEB-DL.H265.DDP5.1-ColorWEB\\Born.with.Luck.S01E08-E09.mkv",
            "F:\\迅雷下载");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Season.Should().Be(1, "S01 应识别为季号");
        r.Episode.Should().Be(8, "S01E08-E09 应识别 episode=8");
        r.EpisodeEnd.Should().Be(9, "S01E08-E09 应识别 episodeEnd=9");
        r.Year.Should().Be(2026, "父目录 2026 应识别为年份");
        r.MediaType.Should().Be("tv");
        r.Title.Should().NotBeNullOrWhiteSpace();
        // 噪声（PTHDTV.com、2160p、IQ、WEB-DL、H265、DDP5.1、-ColorWEB）应被清洗掉
        r.Title.Should().NotContain("PTHDTV");
        r.Title.Should().NotContain("ColorWEB");
        r.Title.Should().NotContain("2160p");
    }

    [Fact]
    public async Task EpisodeRange_ChineseBracket_Identified()
    {
        // 单独「[第08-09集]」格式应能识别 episode + episodeEnd
        FileParseContext ctx = FileParseContext.FileNameOnly("某剧 [第08-09集].mkv");
        RuleParseResult r = await _sut.ParseAsync(ctx);
        r.Episode.Should().Be(8);
        r.EpisodeEnd.Should().Be(9);
    }

    [Fact]
    public async Task SeasonOnly_NoEpisode_IdentifiesSeason()
    {
        // 父目录 PT 站常见命名 Sxx 后没有 E
        FileParseContext ctx = FileParseContext.FileNameOnly("Born.with.Luck.S01.2026.2160p.mkv");
        RuleParseResult r = await _sut.ParseAsync(ctx);
        r.Season.Should().Be(1);
        r.Year.Should().Be(2026);
    }

    [Fact]
    public async Task GroupBracket_FullWidth_Stripped()
    {
        // 全角【】方括号块应被剥离
        FileParseContext ctx = FileParseContext.FileNameOnly("【高清剧集网发布 www.PTHDTV.com】低智商犯罪 2026.mkv");
        RuleParseResult r = await _sut.ParseAsync(ctx);
        r.Title.Should().Contain("低智商犯罪");
        r.Title.Should().NotContain("PTHDTV");
        r.Year.Should().Be(2026);
    }

    [Fact]
    public async Task ReleaseGroupSuffix_Stripped_FromTitle()
    {
        FileParseContext ctx = FileParseContext.FileNameOnly("Some.Movie.2020.1080p.WEB-DL-ColorWEB.mkv");
        RuleParseResult r = await _sut.ParseAsync(ctx);
        r.Title.Should().NotContain("ColorWEB");
        r.Year.Should().Be(2020);
    }

    // ---------- 总季数 / 总集数后缀清洗（TotalCountNoise）----------

    [Theory]
    [InlineData("国务卿女士 6季", "国务卿女士")]      // 裸 N季（无「第」前缀）
    [InlineData("扫毒 全24集", "扫毒")]               // 全N集
    [InlineData("某剧 共26集", "某剧")]               // 共N集
    [InlineData("复仇者 6部", "复仇者")]              // 裸 N部
    [InlineData("某剧 第1-6季", "某剧")]              // 区间季 = 总季数跨度
    [InlineData("某剧 第1~6季", "某剧")]              // 区间季（全角波浪号变体用半角 ~）
    [InlineData("纪录片 (全24集)", "纪录片")]         // 半角括号包裹
    [InlineData("纪录片 （全24集）", "纪录片")]        // 全角括号包裹
    public void TotalCountNoise_Strips_TotalSuffix(string input, string expectedCore)
    {
        // 直接验证正则：剥掉总量后缀后 Trim 应只剩标题主体
        string stripped = BuiltinRulesCatalog.TotalCountNoise.Replace(input, " ").Trim();
        stripped.Should().Be(expectedCore);
    }

    [Theory]
    [InlineData("庆余年 第3季")]   // 单季季号必须保留给季号提取，不被裸总数规则误吞
    [InlineData("鬼灭之刃 第8集")] // 单集集号同理保留
    [InlineData("低智商犯罪 第08-09集")] // 中文双集区间：末段「09集」不能被裸规则剥
    [InlineData("战狼2")]          // 标题里的数字（无 季/集/部 后缀）不动
    [InlineData("全职高手")]       // 「全」后非数字，不是总量标记
    [InlineData("共和国")]         // 「共」后非数字，不是总量标记
    public void TotalCountNoise_Preserves_SeasonEpisodeAndTitleDigits(string input)
    {
        // 不匹配 → Replace 原样返回
        string result = BuiltinRulesCatalog.TotalCountNoise.Replace(input, " ");
        result.Should().Be(input, "单季「第N季」/ 单集「第N集」/ 区间末段 / 标题数字均不应被总量规则剥离");
    }

    [Fact]
    public async Task Builtin_TotalEpisodeCount_StrippedFromTitle()
    {
        // 「全24集」是总集数，不该污染送 TMDB 的标题；走内置规则（无用户规则）
        FileParseContext ctx = FileParseContext.FileNameOnly("庆余年 全24集.mkv");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Title.Should().Be("庆余年", "「全24集」总集数后缀应被清洗，仅剩剧名");
        r.Title.Should().NotContain("24");
    }

    [Fact]
    public async Task Builtin_SingleSeason_NotStrippedAsTotalCount()
    {
        // 「第3季」是单季季号，必须交给季号提取（season=3），不能被总量清洗规则误吞为噪声
        FileParseContext ctx = FileParseContext.FromFullPath(
            "C:\\watch\\庆余年 第3季\\第05集.mkv", "C:\\watch");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Season.Should().Be(3, "「第3季」应被识别为季号，而非被总量规则当噪声剥掉");
        r.Episode.Should().Be(5, "文件名「第05集」给出集号");
        r.MediaType.Should().Be("tv");
        r.Title.Should().Be("庆余年", "标题取祖父目录、季号边界裁掉后是干净剧名");
    }

    // ---------- 「Season NN」目录段季号（SeasonWordLatin）----------

    [Fact]
    public async Task Builtin_SeasonWordDir_StandardLayout_SeasonFromDirEpisodeFromStem()
    {
        // 标准目录布局：Show Name (2020)/Season 02/07.mkv —— 季号只在「Season 02」目录段，文件名只剩集号
        FileParseContext ctx = FileParseContext.FromFullPath(
            "C:\\watch\\Show Name (2020)\\Season 02\\07.mkv", "C:\\watch");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Season.Should().Be(2, "「Season 02」目录段应识别为季号");
        r.Episode.Should().Be(7, "纯数字文件名 07 应识别为集号");
        r.Year.Should().Be(2020, "年份从剧名目录段提取");
        r.MediaType.Should().Be("tv");
        r.Title.Should().StartWith("Show Name");
    }

    [Fact]
    public async Task Builtin_SeasonWordDir_DotSeparator_SeasonParsed()
    {
        FileParseContext ctx = FileParseContext.FromFullPath(
            "C:\\watch\\Season.03\\EP05.mkv", "C:\\watch");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Season.Should().Be(3, "「Season.03」目录段应识别为季号");
        r.Episode.Should().Be(5);
        r.MediaType.Should().Be("tv");
    }

    [Theory]
    [InlineData("Season 02", true, 2)]
    [InlineData("season.3", true, 3)]      // 忽略大小写
    [InlineData("Season_10", true, 10)]
    [InlineData("Season 2020", false, 0)]  // 年份不可截成季号（数字后还有数字 → 整体不命中）
    [InlineData("Preseason 02", false, 0)] // 单词前缀守护（前面是字母不命中）
    [InlineData("Seasons 1-6", false, 0)]  // 复数（总量语义）不命中
    public void SeasonWordLatin_Regex_Boundary(string input, bool shouldMatch, int expectedSeason)
    {
        Match m = BuiltinRulesCatalog.SeasonWordLatin.Match(input);
        m.Success.Should().Be(shouldMatch);
        if (shouldMatch)
        {
            int.Parse(m.Groups["season"].Value).Should().Be(expectedSeason);
        }
    }

    // ---------- 中文噪声目录段不胜出为标题（Noise 中文纯噪声词）----------

    [Fact]
    public async Task Builtin_CjkNoiseDirSegment_NotSelectedAsTitle()
    {
        // 「正片」是下载站常见目录层级，纯噪声不能胜出为标题；真实剧名在更外层目录
        FileParseContext ctx = FileParseContext.FromFullPath(
            "F:\\迅雷下载\\三体.2023.S01.2160p\\正片\\S01E05.mkv", "F:\\迅雷下载");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Title.Should().Be("三体", "「正片」层是纯噪声，应继续向外层取真实标题");
        r.Season.Should().Be(1);
        r.Episode.Should().Be(5);
        r.Year.Should().Be(2023);
        r.MediaType.Should().Be("tv");
    }

    [Theory]
    [InlineData("正片", "")]                       // 单个噪声词整段剥除
    [InlineData("国语中字", "")]                   // 纯噪声连写串整段剥除
    [InlineData("蓝光原盘", "")]
    [InlineData("双语字幕", "")]
    [InlineData("高清剧集网", "高清剧集网")]        // 与真实词粘连：CJK 词边界不成立，不误伤
    [InlineData("全职高手", "全职高手")]            // 真实标题以噪声词首字开头，不误伤
    [InlineData("数码宝贝全集", "数码宝贝全集")]    // 噪声词粘在标题尾部：保守不剥
    public void Noise_CjkWords_StripStandaloneOnly(string input, string expected)
    {
        BuiltinRulesCatalog.Noise.Replace(input, " ").Trim().Should().Be(expected);
    }

    // ---------- 小数集 SxxEyy.5 守护 ----------

    [Fact]
    public async Task Builtin_FractionalEpisode_NotTruncatedToInteger_LowConfidence()
    {
        // E11.5 回顾/特别篇若截成 E11 会与正片第 11 集同号冲突（Overwrite 策略下最坏覆盖正片）
        RuleParseResult r = await Parse("Re.Zero.S01E11.5.Recap.mkv", parentFolderName: null);

        r.Episode.Should().BeNull("小数集 E11.5 不得截成整数集 11");
        r.Season.Should().Be(1, "季号保留供 AI 参考");
        r.Confidence.Should().BeLessThan(0.6, "必须低置信走 AI / 人工审核");
    }

    [Fact]
    public async Task Builtin_FractionalEpisode_AtStemEnd_AlsoGuarded()
    {
        RuleParseResult r = await Parse("Show.S02E08.5.mkv", parentFolderName: null);

        r.Episode.Should().BeNull("串尾「.5」同属小数集形态");
        r.Season.Should().Be(2);
        r.Confidence.Should().BeLessThan(0.6);
    }

    [Theory]
    [InlineData("Show.S01E02.1080p.mkv", 2)] // 多位数字技术参数不是小数尾巴
    [InlineData("Show.S01E02.4K.mkv", 2)]    // 数字后跟字母（4K）不是小数尾巴
    [InlineData("Show.S01E02.2008.mkv", 2)]  // 4 位年份不是小数尾巴
    public async Task Builtin_TechTokenAfterEpisode_NotMistakenAsFraction(string fileName, int expectedEpisode)
    {
        RuleParseResult r = await Parse(fileName, parentFolderName: null);

        r.Episode.Should().Be(expectedEpisode);
        r.Season.Should().Be(1);
    }

    // ---------- 小数集 EP11.5 / 11.5 守护（episode-only 无季号形态）----------

    [Fact]
    public async Task Builtin_FractionalEpisode_EpisodeOnlyForm_NotTruncated()
    {
        // EP11.5（无季号形态）同样不得截成 E11；缺季缺集 → 低置信走 AI / 人工审核
        RuleParseResult r = await Parse("EP11.5.mkv", parentFolderName: null);

        r.Episode.Should().BeNull("小数集 EP11.5 不得截成整数集 11");
        r.Confidence.Should().BeLessThan(0.6, "必须低置信走 AI / 人工审核");
    }

    [Fact]
    public async Task Builtin_FractionalEpisode_EpisodeOnlyForm_WithSeasonDir_NotDirectArchive()
    {
        // 「Season 01」目录补出季号后，若 EP11.5 被截成 E11 会以 0.85 直通归档、与正片第 11 集同号冲突
        RuleParseResult r = await Parse("EP11.5.mkv", parentFolderName: "Season 01");

        r.Episode.Should().BeNull("小数集 EP11.5 不得截成整数集 11");
        r.Season.Should().Be(1, "季号保留供 AI 参考");
        r.Confidence.Should().BeLessThan(0.6, "缺集号必须压到低置信，不得 0.85 直通归档");
    }

    [Fact]
    public async Task Builtin_FractionalEpisode_PureNumericStem_WithSeasonDir_NotDirectArchive()
    {
        // 纯数字小数文件名「11.5.mkv」：stem「11.5」非纯整数，不得当作第 11 集；组合季目录后不得直通
        FileParseContext ctx = FileParseContext.FromFullPath(
            "C:\\watch\\Show Name (2020)\\Season 01\\11.5.mkv", "C:\\watch");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Episode.Should().BeNull("「11.5」是半集，不得截成第 11 集");
        r.Season.Should().Be(1, "「Season 01」目录段给出季号");
        r.Confidence.Should().BeLessThan(0.6, "缺集号必须压到低置信，不得直通归档");
    }

    [Fact]
    public async Task Builtin_FractionalEpisode_ExtensionlessNumericStem_Guarded()
    {
        // 无扩展名文件「11.5」：GetFileNameWithoutExtension 会把「.5」当扩展名剥掉、stem 截成「11」，
        // 纯数字兜底不得再把它当第 11 集
        FileParseContext ctx = FileParseContext.FromFullPath(
            "C:\\watch\\Show Name (2020)\\Season 01\\11.5", "C:\\watch");
        RuleParseResult r = await _sut.ParseAsync(ctx);

        r.Episode.Should().BeNull("stem 被扩展名剥离截断的「11.5」不得当作第 11 集");
        r.Season.Should().Be(1);
        r.Confidence.Should().BeLessThan(0.6);
    }

    [Theory]
    [InlineData("EP11.1080p.mkv")] // 多位数字技术参数不是小数尾巴
    [InlineData("EP11.5v2.mkv")]   // 数字后跟字母（v2 版本标记）不是小数尾巴，口径与 SxxEyy 检测一致
    public async Task Builtin_EpisodeOnly_TechTokenAfterEpisode_NotMistakenAsFraction(string fileName)
    {
        RuleParseResult r = await Parse(fileName, parentFolderName: null);

        r.Episode.Should().Be(11, "技术 token 不是小数尾巴，集号照常提取");
    }

    [Fact]
    public async Task Builtin_FractionalEpisode_EpisodeOnlyRange_AlsoGuarded()
    {
        // 双集范围末端带小数尾巴（EP08-09.5）：episodeEnd 数字后是小数 → 整组丢弃转低置信
        RuleParseResult r = await Parse("Show.EP08-09.5.mkv", parentFolderName: null);

        r.Episode.Should().BeNull();
        r.EpisodeEnd.Should().BeNull();
        r.Confidence.Should().BeLessThan(0.6);
    }

    // ---------- 直连双集 S01E08E09（无连字符）----------

    [Theory]
    [InlineData("Born.with.Luck.S01E08E09.mkv", 1, 8, 9)]  // 直连无分隔
    [InlineData("Some.Show.S02EP01EP02.mkv", 2, 1, 2)]     // EP 变体
    [InlineData("Born.with.Luck.S01E08-E09.mkv", 1, 8, 9)] // 既有范围标记形态不回归
    public async Task Builtin_DirectDoubleEpisode_Parsed(string fileName, int season, int episode, int episodeEnd)
    {
        RuleParseResult r = await Parse(fileName, parentFolderName: null);

        r.Season.Should().Be(season);
        r.Episode.Should().Be(episode);
        r.EpisodeEnd.Should().Be(episodeEnd);
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task Builtin_DirectDoubleEpisode_EndLessThanStart_Dropped()
    {
        // end < start 视为非法范围：丢弃 end，保留单集 start
        RuleParseResult r = await Parse("Show.S01E09E08.mkv", parentFolderName: null);

        r.Episode.Should().Be(9);
        r.EpisodeEnd.Should().BeNull();
    }

    // ---------- 种子规则 V2：综艺日期 / AKA / Anime 英文季号 / 全N集 / Episode N / 第N章回 ----------

    [Fact]
    public async Task SeedRule_VarietyDateWithSeason_ExtractsYearAndMmdd()
    {
        SeedRule(new ParseRule
        {
            Name = "综艺第N季 + 日期作集",
            Enabled = true,
            Priority = 22,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<season>\d{1,2})季[\s\._\-]+(?<year>20\d{2})(?<episode>\d{4})(?:[\s\._\-]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("极限挑战.第7季.20210501.1080p.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("极限挑战");
        r.Season.Should().Be(7);
        r.Year.Should().Be(2021);
        r.Episode.Should().Be(501, "MMDD = 0501 → episode=501");
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task SeedRule_VarietyDateOnly_ExtractsYearAndMmdd()
    {
        SeedRule(new ParseRule
        {
            Name = "综艺日期作集",
            Enabled = true,
            Priority = 28,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+(?<year>20\d{2})(?<episode>\d{4})(?:[\s\._\-]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("Running.Man.20230101.HDTV.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Running Man", "分隔符折叠后空格");
        r.Year.Should().Be(2023);
        r.Episode.Should().Be(101, "MMDD = 0101 → episode=101");
    }

    [Fact]
    public async Task SeedRule_AkaTitle_TakesPrimaryBeforeAka()
    {
        SeedRule(new ParseRule
        {
            Name = "AKA 多语言标题",
            Enabled = true,
            Priority = 32,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+(?:AKA|aka|a\.k\.a\.|也叫)[\s\._\-]+.+?[\s\._\-]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("Vagabond.AKA.바가본드.S01E05.1080p.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Vagabond", "取 AKA 前的主标题，不取韩文别名");
        r.Season.Should().Be(1);
        r.Episode.Should().Be(5);
    }

    [Fact]
    public async Task SeedRule_AnimeNthSeason_ExtractsSeasonNumber()
    {
        SeedRule(new ParseRule
        {
            Name = "Anime 英文季号 (Nth Season)",
            Enabled = true,
            Priority = 48,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+(?<season>\d{1,2})(?:st|nd|rd|th)[\s\._\-]?Season[\s\._\-]+(?:E|EP|Episode)?[\s\._\-]?(?<episode>\d{1,4})",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("Attack on Titan 3rd Season E12.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Attack on Titan");
        r.Season.Should().Be(3);
        r.Episode.Should().Be(12);
    }

    [Fact]
    public async Task SeedRule_FullSeasonPack_IdentifiesTv_WithoutEpisode()
    {
        // 与 DataSeeder 同 Pattern：「全N集」的 N 是总集数不是集号，不捕获 episode，
        // 否则「扫毒.全30集」会归档成 S01E30 误指向第 30 集
        SeedRule(new ParseRule
        {
            Name = "全N集 整季合集",
            Enabled = true,
            Priority = 65,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+全\d{1,4}集",
            DefaultType = "tv", ForceType = true,
            ConfidenceBonus = 0.0,
        });

        RuleParseResult r = await Parse("扫毒.全30集.HD.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("扫毒");
        r.Episode.Should().BeNull("总集数 30 不得当作集号");
        r.Season.Should().BeNull();
        r.MediaType.Should().Be("tv");
        r.Confidence.Should().Be(0.50, "tv 缺季集压到 0.50 走 AI / 人工审核");
    }

    [Fact]
    public async Task SeedRule_EpisodeFullWord_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "Episode N 完整英文",
            Enabled = true,
            Priority = 75,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+Episode[\s\._\-]?(?<episode>\d{1,4})(?:[\s\._\-]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.0,
        });

        RuleParseResult r = await Parse("Some Show Episode 12 1080p.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Some Show");
        r.Episode.Should().Be(12);
    }

    [Fact]
    public async Task SeedRule_ChineseChapter_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "中文章节集号「第N章 / 第N回」",
            Enabled = true,
            Priority = 80,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})[章回段]",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.0,
        });

        // 「第N回」
        RuleParseResult r1 = await Parse("天龙八部.第10回.HDTV.mkv", parentFolderName: null);
        r1.MatchedRuleId.Should().NotBeNull();
        r1.Title.Should().Be("天龙八部");
        r1.Episode.Should().Be(10);

        // 「第N章」
        RuleParseResult r2 = await Parse("三体.第1章.mkv", parentFolderName: null);
        r2.MatchedRuleId.Should().NotBeNull();
        r2.Title.Should().Be("三体");
        r2.Episode.Should().Be(1);
    }

    // ---------- 种子规则 V3：方括号双段季集 / 第N部 第N集 / 第N期 / Part-Cour / Vol / #N No.N / 第N集兜底 / YYMMDD ----------

    [Fact]
    public async Task SeedRule_BracketDualSeasonEpisode_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "方括号双段季集 [Sxx][Eyy]",
            Enabled = true,
            Priority = 27,
            Scope = ParseScope.FileName,
            Pattern = @"\[[Ss](?<season>\d{1,2})\]\s*\[[Ee][Pp]?(?<episode>\d{1,4})\]",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        // [字幕组][番名][S01][E12][1080p] —— title 不抓，让 CleanedStem 兜底（GroupBracket 剥方括号块后取剩余）
        RuleParseResult r = await Parse("[BeanSub][Shows][S01][E12][1080p].mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Season.Should().Be(1);
        r.Episode.Should().Be(12);
        r.MediaType.Should().Be("tv");
    }

    [Fact]
    public async Task SeedRule_ChinesePartEpisode_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "中文「第N部 第N集」",
            Enabled = true,
            Priority = 26,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<season>\d{1,2})部[\s\._\-]+第(?<episode>\d{1,4})[集话話]",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("我的天才女友.第2部.第12集.1080p.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("我的天才女友");
        r.Season.Should().Be(2);
        r.Episode.Should().Be(12);
    }

    [Fact]
    public async Task SeedRule_VarietyQi_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "综艺「第N期」",
            Enabled = true,
            Priority = 29,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})期",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        RuleParseResult r = await Parse("Knowing Bros 第300期.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Knowing Bros");
        r.Episode.Should().Be(300);
    }

    [Fact]
    public async Task SeedRule_AnimePartCour_ExtractsTitleAndEpisode()
    {
        SeedRule(new ParseRule
        {
            Name = "番剧分卷 Part / Cour + 集号",
            Enabled = true,
            Priority = 49,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+(?:(?:Part|Cour)[\s\._\-]?\d{1,2}|\d(?:st|nd|rd|th)[\s\._\-]?Cour)[\s\._\-]+(?:E|EP|Episode)[\s\._\-]?(?<episode>\d{1,4})",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.05,
        });

        // Part N E01
        RuleParseResult r1 = await Parse("Mob Psycho 100 Part 2 E03.mkv", parentFolderName: null);
        r1.MatchedRuleId.Should().NotBeNull();
        r1.Title.Should().Be("Mob Psycho 100");
        r1.Episode.Should().Be(3);

        // 2nd Cour E05 —— 注意 `-` 是分隔符之一会被折叠为空格（用户规则 title 折叠语义）
        RuleParseResult r2 = await Parse("Kaguya-sama Love is War 2nd Cour E05.mkv", parentFolderName: null);
        r2.MatchedRuleId.Should().NotBeNull();
        r2.Title.Should().Be("Kaguya sama Love is War");
        r2.Episode.Should().Be(5);
    }

    [Fact]
    public async Task SeedRule_AnimeVolume_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "动漫 Vol / Volume 卷集号",
            Enabled = true,
            Priority = 52,
            Scope = ParseScope.FileName,
            Pattern = @"^(?:\[[^\]]{1,40}\]\s*)*(?<title>[^\[\]]+?)[\s\._\-]+Vol(?:ume)?\.?[\s\._\-]?(?<episode>\d{1,3})(?:[\s\._\-\[]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.0,
        });

        // Vol.NN 短格式
        RuleParseResult r1 = await Parse("[VCB-Studio] 进击的巨人 Vol.01 [BDRip].mkv", parentFolderName: null);
        r1.MatchedRuleId.Should().NotBeNull();
        r1.Title.Should().Be("进击的巨人");
        r1.Episode.Should().Be(1);

        // Volume N 全词
        RuleParseResult r2 = await Parse("Some Show Volume 3 BDBox.mkv", parentFolderName: null);
        r2.MatchedRuleId.Should().NotBeNull();
        r2.Title.Should().Be("Some Show");
        r2.Episode.Should().Be(3);
    }

    [Fact]
    public async Task SeedRule_AbsoluteEpisodeHashOrNo_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "绝对集号「#N / No.N」",
            Enabled = true,
            Priority = 82,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+(?:#|No\.)[\s\._]?(?<episode>\d{1,4})(?:[\s\._\-]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.0,
        });

        // #N
        RuleParseResult r1 = await Parse("One Piece #1000.mkv", parentFolderName: null);
        r1.MatchedRuleId.Should().NotBeNull();
        r1.Title.Should().Be("One Piece");
        r1.Episode.Should().Be(1000);

        // No.N
        RuleParseResult r2 = await Parse("海贼王 No.1100.mkv", parentFolderName: null);
        r2.MatchedRuleId.Should().NotBeNull();
        r2.Title.Should().Be("海贼王");
        r2.Episode.Should().Be(1100);
    }

    [Fact]
    public async Task SeedRule_ChineseEpisodeNoSeasonFallback_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "无季号「第N集」中文兜底",
            Enabled = true,
            Priority = 83,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})[集话話](?:[\s\._\-]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.0,
        });

        // 「集」
        RuleParseResult r1 = await Parse("鬼灭之刃.第3集.1080p.mkv", parentFolderName: null);
        r1.MatchedRuleId.Should().NotBeNull();
        r1.Title.Should().Be("鬼灭之刃");
        r1.Episode.Should().Be(3);

        // 「话」
        RuleParseResult r2 = await Parse("进击的巨人 第12话.mkv", parentFolderName: null);
        r2.MatchedRuleId.Should().NotBeNull();
        r2.Title.Should().Be("进击的巨人");
        r2.Episode.Should().Be(12);
    }

    [Fact]
    public async Task SeedRule_VarietyShortDate_Matches()
    {
        SeedRule(new ParseRule
        {
            Name = "综艺 YYMMDD 短日期作集",
            Enabled = true,
            Priority = 85,
            Scope = ParseScope.FileName,
            Pattern = @"^(?<title>.+?)[\s\._\-]+(?<episode>[12]\d{5})(?:[\s\._\-]|$)",
            DefaultType = "tv", ForceType = false,
            ConfidenceBonus = 0.0,
        });

        RuleParseResult r = await Parse("Running.Man.210501.HDTV.mkv", parentFolderName: null);

        r.MatchedRuleId.Should().NotBeNull();
        r.Title.Should().Be("Running Man", "分隔符折叠后空格");
        r.Episode.Should().Be(210501, "YYMMDD 整体作 episode");
    }

    // ---------- DataSeeder 集成验证 ----------

    [Fact]
    public async Task SeedRuleCount_AfterAllSeedsApplied_Equals23()
    {
        // 直接通过 DataSeeder 跑一次种子；验证 V1+V2+V3 合计 23 条规则到位
        using PmmDbContext db = _dbFactory.CreateDbContext();
        Setup.DataSeeder seeder = new(
            _dbFactory,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<Setup.DataSeeder>());
        await seeder.SeedAsync();

        int count = await db.ParseRules.CountAsync();
        count.Should().Be(23, "V1 8 条 + V2 7 条 + V3 8 条 = 23");

        // 抽查 V2 引入的 7 个名字都在
        string[] expectedV2Names =
        {
            "综艺第N季 + 日期作集",
            "综艺日期作集",
            "AKA 多语言标题",
            "Anime 英文季号 (Nth Season)",
            "全N集 整季合集",
            "Episode N 完整英文",
            "中文章节集号「第N章 / 第N回」",
        };
        // 抽查 V3 引入的 8 个名字都在
        string[] expectedV3Names =
        {
            "方括号双段季集 [Sxx][Eyy]",
            "中文「第N部 第N集」",
            "综艺「第N期」",
            "番剧分卷 Part / Cour + 集号",
            "动漫 Vol / Volume 卷集号",
            "绝对集号「#N / No.N」",
            "无季号「第N集」中文兜底",
            "综艺 YYMMDD 短日期作集",
        };
        List<string> existing = await db.ParseRules.Select(r => r.Name).ToListAsync();
        foreach (string name in expectedV2Names)
        {
            existing.Should().Contain(name, $"V2 种子规则缺失：{name}");
        }
        foreach (string name in expectedV3Names)
        {
            existing.Should().Contain(name, $"V3 种子规则缺失：{name}");
        }
    }

    // ---------- 目录上下文标题回落 + 本地备选搜索标题（减少 AI 参与）----------

    [Fact]
    public async Task Builtin_TitleFromParent_WhenStemIsTechResidueOnly()
    {
        // 真实日志复现：文件名无标题（S01E01 打头全是技术参数），剧名只在父目录——
        // 旧行为把清洗残渣「2026 60fps WEB 1」当标题以 0.95 直查 TMDB 零候选后白走一遍 AI
        RuleParseResult r = await Parse(
            "S01E01.2026.2160p.60fps.WEB-DL.H265.10bit.DDP.5.1.mkv", parentFolderName: "南部档案");

        r.Title.Should().Be("南部档案", "文件名层无有效标题内容，应回落父目录取剧名");
        r.Season.Should().Be(1);
        r.Episode.Should().Be(1);
        r.Year.Should().Be(2026);
        r.MediaType.Should().Be("tv");
        r.Confidence.Should().Be(0.95);
        r.HasSpecialChars.Should().BeFalse();
    }

    [Fact]
    public async Task Builtin_PureEpisodeStem_TitleFromParent_Confidence070()
    {
        // 纯集号文件名（07.mkv）：标题取父目录；tv 仅缺季 → 0.70 走 TMDB 直查（单季剧由下游自动补季）
        RuleParseResult r = await Parse("07.mkv", parentFolderName: "菜鸟炊事兵");

        r.Title.Should().Be("菜鸟炊事兵");
        r.Episode.Should().Be(7);
        r.Season.Should().BeNull();
        r.MediaType.Should().Be("tv");
        r.Confidence.Should().Be(0.70);
    }

    [Theory]
    [InlineData("DACZLNF-09.mkv", 9)]
    [InlineData("YTYHXBYL-30.mkv", 30)]
    public async Task Builtin_ReleaseTagEpisode_EpisodeExtracted_TitleFromParent(string fileName, int expectedEpisode)
    {
        // 「压制代号-集号」整串形态：数字段作集号，字母段不当标题——
        // 旧行为拿 DACZLNF / YTYHXBYL 当标题搜 TMDB 必然零候选，再白走一遍 AI
        RuleParseResult r = await Parse(fileName, parentFolderName: "南部档案");

        r.Title.Should().Be("南部档案", "代号层跳过标题竞选，应回落父目录取剧名");
        r.Episode.Should().Be(expectedEpisode);
        r.MediaType.Should().Be("tv");
        r.Confidence.Should().Be(0.70, "tv 仅缺季（集号在）→ 0.70 直查");
    }

    [Fact]
    public async Task Builtin_TitleDashYear_NotTreatedAsReleaseTag()
    {
        // 「标题-年份」形态（Show-2020）：4 位年份数字段不当集号、该层照常参与标题竞选
        RuleParseResult r = await Parse("Show-2020.mkv", parentFolderName: null);

        r.Title.Should().Be("Show");
        r.Episode.Should().BeNull("2020 是年份不是集号");
        r.Year.Should().Be(2020);
        r.MediaType.Should().Be("movie");
    }

    [Fact]
    public async Task AlternativeTitles_MixedTitle_SplitsIntoCjkAndLatinSegments()
    {
        // 混排标题按现有决策直接走 AI；本地备选标题（CJK 段 / 粘连副标段 / 拉丁词组段）是 AI 前置拦截的弹药
        RuleParseResult r = await Parse(
            "机动战士高达SEEDFREEDOM.Mobile.Suit.Gundam.Seed.Freedom.2024.1080p.KKTV.WEB-DL.mkv",
            parentFolderName: null);

        r.HasSpecialChars.Should().BeTrue();
        r.Year.Should().Be(2024);
        r.AlternativeTitles.Should().NotBeNull();
        r.AlternativeTitles.Should().Contain("Mobile Suit Gundam Seed Freedom", "拉丁词组段可独立搜 TMDB 命中电影条目");
        r.AlternativeTitles![0].Should().Be("机动战士高达", "纯 CJK 段排最前（TMDB 首选语言 zh-CN 命中率最高）");
    }

    [Fact]
    public async Task AlternativeTitles_ChineseNameFromParentFolder()
    {
        // 真实日志复现：英文主标题在 TMDB zh-CN 零候选曾触发 AI；父目录混排名里的中文剧名成为本地备选
        RuleParseResult r = await Parse(
            "Teach.You.a.Lesson.S01E01.Episode.1.1080p.NF.WEB-DL.DDP.5.1.Atmos.H.265-B.mkv",
            parentFolderName: "【高清剧集网发布 www.QQHDTV.com】铁拳教育[全10集][简繁英字幕].Teach.You.a.Lesson.S01.1080p.NF.WEB-DL.DDP.5.1.Atmos.H.265-XXX");

        r.Title.Should().Be("Teach You a Lesson");
        r.Season.Should().Be(1);
        r.Episode.Should().Be(1);
        r.AlternativeTitles.Should().NotBeNull();
        r.AlternativeTitles![0].Should().Be("铁拳教育", "主标题零候选时先试中文名重搜，免走 AI");
    }

    [Fact]
    public async Task AlternativeTitles_SingleLanguageTitle_NoSelfDuplicate()
    {
        // 纯单语标题拆分产物与主标题相同 → 去重后不产出备选（无目录层补充时 AlternativeTitles 为 null）
        RuleParseResult r = await Parse("Inception.2010.1080p.BluRay.x264.mkv", parentFolderName: null);

        (r.AlternativeTitles ?? []).Should().NotContain(r.Title, "备选不应包含主标题自身");
    }

    [Theory]
    [InlineData("国王排名 Ousama Ranking", new[] { "国王排名", "Ousama Ranking" })]
    [InlineData("机动战士高达SEEDFREEDOM Mobile Suit Gundam Seed Freedom", new[] { "机动战士高达", "SEEDFREEDOM", "Mobile Suit Gundam Seed Freedom" })]
    [InlineData("Pure English Title", new[] { "Pure English Title" })]
    [InlineData("纯中文标题", new[] { "纯中文标题" })]
    public void SplitMixedSegments_Cases(string input, string[] expected)
    {
        RuleEngineService.SplitMixedSegments(input).Should().Equal(expected);
    }

    // ---------- helpers ----------

    private long SeedRule(ParseRule rule)
    {
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ParseRules.Add(rule);
        db.SaveChanges();
        return rule.Id;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PmmDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection c) { _connection = c; }
        public PmmDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<PmmDbContext> opts = new();
            opts.UseSqlite(_connection);
            return new PmmDbContext(opts.Options);
        }
    }
}
