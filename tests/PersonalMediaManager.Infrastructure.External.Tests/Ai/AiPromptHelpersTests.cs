using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

/// <summary>AiPromptHelpers.ParseContent — JSON 反解（重点覆盖第 0 层 aliases 检索别名解析与清洗）</summary>
/// <remarks>
/// aliases 用于「中文 title 在 TMDB 查不到时」逐个兜底检索（国漫/日漫主条目常为原名）。
/// 清洗规则：去空白 → 丢空串 → 不区分大小写去重 → 排除与 title 雷同 → 至多 3 条；全空回 null。
/// </remarks>
public sealed class AiPromptHelpersTests
{
    [Fact]
    public void ParseContent_Extracts_Aliases_In_Order()
    {
        AiParseResult r = AiPromptHelpers.ParseContent(
            "{\"title\":\"鬼灭之刃\",\"type\":\"tv\",\"confidence\":0.9,\"aliases\":[\"鬼滅の刃\",\"Demon Slayer\"]}");
        r.Title.Should().Be("鬼灭之刃");
        r.SearchAliases.Should().Equal("鬼滅の刃", "Demon Slayer");
    }

    [Fact]
    public void ParseContent_Missing_Aliases_Field_Yields_Null()
    {
        AiParseResult r = AiPromptHelpers.ParseContent("{\"title\":\"X\",\"type\":\"movie\",\"confidence\":0.8}");
        r.SearchAliases.Should().BeNull();
    }

    [Fact]
    public void ParseContent_Empty_Aliases_Array_Yields_Null()
    {
        AiParseResult r = AiPromptHelpers.ParseContent("{\"title\":\"X\",\"type\":\"movie\",\"confidence\":0.8,\"aliases\":[]}");
        r.SearchAliases.Should().BeNull();
    }

    [Fact]
    public void ParseContent_Aliases_Dedup_DropBlank_ExcludeTitle_LimitsThree()
    {
        AiParseResult r = AiPromptHelpers.ParseContent(
            "{\"title\":\"Title\",\"type\":\"movie\",\"confidence\":0.8,\"aliases\":[\"Title\",\" \",\"A\",\"a\",\"B\",\"C\",\"D\"]}");
        // "Title" 与主标题雷同剔除；空白串剔除；"A"/"a" 不区分大小写去重；超出 3 条截断
        r.SearchAliases.Should().Equal("A", "B", "C");
    }

    [Fact]
    public void ParseContent_Aliases_NonString_Elements_Skipped()
    {
        AiParseResult r = AiPromptHelpers.ParseContent(
            "{\"title\":\"X\",\"type\":\"movie\",\"confidence\":0.8,\"aliases\":[123,\"Valid\",null]}");
        r.SearchAliases.Should().Equal("Valid");
    }

    // ---------- 数值范围守护：越界置 null（转人工审核，不直通归档炸 ArgumentOutOfRangeException）----------

    [Theory]
    [InlineData("{\"title\":\"X\",\"type\":\"tv\",\"season\":2024,\"episode\":3,\"confidence\":0.9}", null, 3)]
    [InlineData("{\"title\":\"X\",\"type\":\"tv\",\"season\":-1,\"episode\":5,\"confidence\":0.9}", null, 5)]
    [InlineData("{\"title\":\"X\",\"type\":\"tv\",\"season\":2,\"episode\":-1,\"confidence\":0.9}", 2, null)]
    [InlineData("{\"title\":\"X\",\"type\":\"tv\",\"season\":2,\"episode\":99999,\"confidence\":0.9}", 2, null)]
    public void ParseContent_OutOfRange_SeasonEpisode_NulledForReview(string json, int? expectedSeason, int? expectedEpisode)
    {
        AiParseResult r = AiPromptHelpers.ParseContent(json);
        r.Season.Should().Be(expectedSeason, "season 越界 [0,99] 必须置 null 让下游转人工审核");
        r.Episode.Should().Be(expectedEpisode, "episode 越界 [0,9999] 必须置 null 让下游转人工审核");
    }

    [Fact]
    public void ParseContent_OutOfRange_EpisodeEnd_And_Year_Nulled()
    {
        AiParseResult r = AiPromptHelpers.ParseContent(
            "{\"title\":\"X\",\"type\":\"tv\",\"season\":1,\"episode\":8,\"episodeEnd\":99999,\"year\":1500,\"confidence\":0.9}");
        r.EpisodeEnd.Should().BeNull("episodeEnd 越界 [0,9999] 置 null");
        r.Year.Should().BeNull("year 越界 [1900,2100] 置 null");
        r.Season.Should().Be(1);
        r.Episode.Should().Be(8);
    }

    [Fact]
    public void ParseContent_InRange_Boundary_Values_Untouched()
    {
        AiParseResult r = AiPromptHelpers.ParseContent(
            "{\"title\":\"X\",\"type\":\"tv\",\"year\":2008,\"season\":99,\"episode\":9999,\"confidence\":0.9}");
        r.Year.Should().Be(2008);
        r.Season.Should().Be(99);
        r.Episode.Should().Be(9999);
    }

    // ---------- 特别篇 season=0 约定 ----------

    [Fact]
    public void ParseContent_SpecialSeason_Zero_Preserved()
    {
        // 特别篇约定 season=0（Specials 季）必须保留，不被范围守护误伤
        AiParseResult r = AiPromptHelpers.ParseContent(
            "{\"title\":\"进击的巨人\",\"type\":\"tv\",\"season\":0,\"episode\":1,\"confidence\":0.9}");
        r.Season.Should().Be(0);
        r.Episode.Should().Be(1);
    }

    [Fact]
    public void SystemPrompt_Contains_SpecialSeasonZero_Convention()
    {
        // OVA / SP / 特典 / 特别篇 / OAD 等特别篇内容 season 归 0 的约定必须写进系统提示词
        AiPromptHelpers.SystemPrompt.Should().Contain("OVA");
        AiPromptHelpers.SystemPrompt.Should().Contain("OAD");
        AiPromptHelpers.SystemPrompt.Should().Contain("特别篇");
        AiPromptHelpers.SystemPrompt.Should().Contain("season 输出 0");
    }

    // ---------- BuildUserPrompt：文件名 / 文件路径 / 已有排查信息 三组分组（调整1）----------

    [Fact]
    public void BuildUserPrompt_AllThreeGroups_LabeledAndOrdered()
    {
        AiParseRequest req = new(
            FileName: "Show.S02E05.mkv",
            RuleHintTitle: "Show",
            RuleHintYear: 2020,
            RelativeSegments: ["剧名", "Season 2"]);

        string prompt = AiPromptHelpers.BuildUserPrompt(req);

        prompt.Should().Contain("【文件名】").And.Contain("Show.S02E05.mkv");
        prompt.Should().Contain("【文件路径】").And.Contain("剧名").And.Contain("Season 2");
        prompt.Should().Contain("【已有排查信息】").And.Contain("Show").And.Contain("2020");
        prompt.Should().Contain("季", "路径组含「季目录优先」引导语");
        prompt.IndexOf("【文件名】").Should().BeLessThan(prompt.IndexOf("【文件路径】"));
        prompt.IndexOf("【文件路径】").Should().BeLessThan(prompt.IndexOf("【已有排查信息】"));
    }

    [Fact]
    public void BuildUserPrompt_OnlyFileName_OmitsEmptyGroups()
    {
        AiParseRequest req = new(FileName: "Movie.2021.mkv");

        string prompt = AiPromptHelpers.BuildUserPrompt(req);

        prompt.Should().Contain("【文件名】").And.Contain("Movie.2021.mkv");
        prompt.Should().NotContain("【文件路径】", "无路径段时省略该组");
        prompt.Should().NotContain("【已有排查信息】", "无规则提示时省略该组");
    }

    [Fact]
    public void BuildUserPrompt_FallsBackToParentFolderName_WhenNoSegments()
    {
        AiParseRequest req = new(FileName: "x.mkv", ParentFolderName: "老目录");

        string prompt = AiPromptHelpers.BuildUserPrompt(req);

        prompt.Should().Contain("【文件路径】").And.Contain("老目录");
    }

    [Fact]
    public void BuildUserPrompt_IncludesRuleHintTypeSeasonEpisode()
    {
        // 规则引擎已提取的 类型 / 季 / 集 补进「已有排查信息」组供 AI 对比修正
        AiParseRequest req = new(
            FileName: "x.mkv",
            RuleHintTitle: "某剧",
            RuleHintType: "tv",
            RuleHintSeason: 2,
            RuleHintEpisode: 8,
            RuleHintEpisodeEnd: 9);

        string prompt = AiPromptHelpers.BuildUserPrompt(req);

        prompt.Should().Contain("【已有排查信息】");
        prompt.Should().Contain("类型：tv");
        prompt.Should().Contain("季：2");
        prompt.Should().Contain("集：8-9", "双集合并展示为起始-结束");
    }

    [Fact]
    public void BuildUserPrompt_RuleHintType_Unknown_Omitted()
    {
        // type=unknown 无信息量，不入 prompt（避免误导 AI）
        AiParseRequest req = new(FileName: "x.mkv", RuleHintTitle: "某片", RuleHintType: "unknown");

        string prompt = AiPromptHelpers.BuildUserPrompt(req);

        prompt.Should().Contain("标题：某片");
        prompt.Should().NotContain("类型：");
    }

    [Fact]
    public void BuildUserPrompt_SingleEpisode_NoRangeDash()
    {
        // 单集（无 EpisodeEnd）展示纯集号，不带范围横线
        AiParseRequest req = new(FileName: "x.mkv", RuleHintEpisode: 5);

        string prompt = AiPromptHelpers.BuildUserPrompt(req);

        prompt.Should().Contain("集：5").And.NotContain("集：5-");
    }

    [Fact]
    public void SystemPrompt_Contains_PathSeasonGuidance()
    {
        // 调整1：SystemPrompt 补了「路径季目录优先于文件名推断季」引导
        AiPromptHelpers.SystemPrompt.Should().Contain("Season 2").And.Contain("优先于文件名");
    }

    [Fact]
    public void SystemPrompt_Requires_NonEmptyTitle()
    {
        // 数据驱动：无剧名文件曾让 AI 返回空对象 / 空 title 触发 Logical 失败；契约要求 title 非空 + 给最佳猜测
        AiPromptHelpers.SystemPrompt.Should().Contain("必须非空");
        AiPromptHelpers.SystemPrompt.Should().Contain("title=null");
        AiPromptHelpers.SystemPrompt.Should().Contain("最佳猜测");
    }

    [Fact]
    public void SystemPrompt_Forbids_Hallucinated_Year()
    {
        // year 属事实字段：只从文件名 / 路径的字面数字提取，严禁凭作品名用世界知识补发行年（多版本作品会锚错版本）
        AiPromptHelpers.SystemPrompt.Should().Contain("严禁凭作品名");
        AiPromptHelpers.SystemPrompt.Should().Contain("多个年份的版本");
    }

    // ---------- GroundYear：AI 幻觉年份的字面溯源守护（文件名 / 路径没写的年份 → 置 null）----------

    [Fact]
    public void GroundYear_Drops_Year_Absent_From_FileName_And_Path()
    {
        // 攻壳机动队 2026 版：文件名无年份，AI 凭名字幻觉出最早剧场版年 1995 → 溯源不到 → 置 null
        AiParseResult ai = new("攻壳机动队", 1995, "movie", null, null, null, 0.8);
        AiParseRequest req = new(FileName: "攻壳机动队.mkv");

        AiParseResult grounded = AiPromptHelpers.GroundYear(ai, req);

        grounded.Year.Should().BeNull("文件名与路径均无 1995 字样，判为 AI 凭空推断");
        grounded.Title.Should().Be("攻壳机动队", "仅剔除年份，标题等其余字段不动");
    }

    [Theory]
    [InlineData("攻壳机动队(2026).mkv")]
    [InlineData("Ghost.in.the.Shell.2026.1080p.mkv")]
    [InlineData("攻壳机动队 2026年剧场版.mkv")]
    public void GroundYear_Keeps_Year_Present_In_FileName(string fileName)
    {
        // 文件名字面写了 2026：属实提取，保留
        AiParseResult ai = new("攻壳机动队", 2026, "movie", null, null, null, 0.9);
        AiParseResult grounded = AiPromptHelpers.GroundYear(ai, new AiParseRequest(FileName: fileName));
        grounded.Year.Should().Be(2026);
    }

    [Fact]
    public void GroundYear_Keeps_Year_Present_In_PathSegment()
    {
        // 年份写在父目录（剧集文件名常只有 EP 号，年份在剧名目录）→ 属实，保留
        AiParseResult ai = new("某剧", 2019, "tv", 1, 3, null, 0.9);
        AiParseRequest req = new(FileName: "EP03.mkv", RelativeSegments: ["某剧 (2019)", "Season 1"]);

        AiPromptHelpers.GroundYear(ai, req).Year.Should().Be(2019);
    }

    [Fact]
    public void GroundYear_Keeps_Year_Present_In_ParentFolderName()
    {
        AiParseResult ai = new("某片", 2015, "movie", null, null, null, 0.9);
        AiParseRequest req = new(FileName: "movie.mkv", ParentFolderName: "某片 2015");
        AiPromptHelpers.GroundYear(ai, req).Year.Should().Be(2015);
    }

    [Fact]
    public void GroundYear_Rejects_Year_That_Is_Substring_Of_Longer_Digits()
    {
        // 1995 只是更长数字串 119952 的子串（非独立年份）→ 不算字面出现 → 置 null，防误放行
        AiParseResult ai = new("X", 1995, "movie", null, null, null, 0.7);
        AiParseResult grounded = AiPromptHelpers.GroundYear(ai, new AiParseRequest(FileName: "119952.mkv"));
        grounded.Year.Should().BeNull();
    }

    [Fact]
    public void GroundYear_Null_Year_PassesThrough_Unchanged()
    {
        AiParseResult ai = new("X", null, "movie", null, null, null, 0.7);
        AiParseResult grounded = AiPromptHelpers.GroundYear(ai, new AiParseRequest(FileName: "x.mkv"));
        grounded.Should().BeSameAs(ai, "year 本就为 null 时原样返回，不重建对象");
    }
}
