using PersonalMediaManager.Application.Common.Archiving;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>PlexNamingConventions（D4.2）— 电影 / 剧集 / 特别篇 / 多版本 / 海报 / nfo 路径生成 + 文件名清洗</summary>
public sealed class PlexNamingConventionsTests
{
    [Fact]
    public void MovieFolder_PlainTitle_FormatsWithYear()
    {
        PlexNamingConventions.MovieFolder("盗梦空间", 2010).Should().Be("盗梦空间 (2010)");
    }

    [Fact]
    public void MovieFileBase_NoEdition_MatchesFolder()
    {
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010).Should().Be("盗梦空间 (2010)");
    }

    [Fact]
    public void MovieFileBase_WithEdition_AppendsAsSuffix()
    {
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010, "Director's Cut")
            .Should().Be("盗梦空间 (2010) - Director's Cut");
    }

    [Fact]
    public void MovieFilePath_PlatformSeparator_CorrectExtensionLowered()
    {
        string p = PlexNamingConventions.MovieFilePath("盗梦空间", 2010, "MKV");
        p.Should().Be($"盗梦空间 (2010){Path.DirectorySeparatorChar}盗梦空间 (2010).mkv");
    }

    [Fact]
    public void MovieFilePath_LeadingDotInExtension_Stripped()
    {
        PlexNamingConventions.MovieFilePath("X", 2020, ".jpg")
            .Should().EndWith(".jpg").And.NotContain("..jpg");
    }

    [Fact]
    public void TvShowFolder_SameFormulaAsMovie()
    {
        PlexNamingConventions.TvShowFolder("绝命毒师", 2008).Should().Be("绝命毒师 (2008)");
    }

    [Fact]
    public void SeasonFolder_PadsToTwoDigits()
    {
        PlexNamingConventions.SeasonFolder(1).Should().Be("Season 01");
        PlexNamingConventions.SeasonFolder(10).Should().Be("Season 10");
        PlexNamingConventions.SeasonFolder(0).Should().Be("Season 00", "S00 = 特别篇 / OVA");
    }

    [Fact]
    public void SeasonFolder_OutOfRange_Throws()
    {
        Action a = () => PlexNamingConventions.SeasonFolder(-1);
        Action b = () => PlexNamingConventions.SeasonFolder(100);
        a.Should().Throw<ArgumentOutOfRangeException>();
        b.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TvEpisodeFileBase_FormatsAllParts()
    {
        PlexNamingConventions.TvEpisodeFileBase("绝命毒师", 2008, 1, 1)
            .Should().Be("绝命毒师 (2008) - S01E01");
        PlexNamingConventions.TvEpisodeFileBase("绝命毒师", 2008, 12, 99)
            .Should().Be("绝命毒师 (2008) - S12E99");
    }

    [Fact]
    public void TvEpisodeFilePath_NestedSeasonFolder()
    {
        string p = PlexNamingConventions.TvEpisodeFilePath("绝命毒师", 2008, 1, 1, "mkv");
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"绝命毒师 (2008){s}Season 01{s}绝命毒师 (2008) - S01E01.mkv");
    }

    [Fact]
    public void Special_Season00_OvasInS00()
    {
        string p = PlexNamingConventions.TvEpisodeFilePath("我的英雄学院", 2016, 0, 5, "mkv");
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"我的英雄学院 (2016){s}Season 00{s}我的英雄学院 (2016) - S00E05.mkv");
    }

    // ---------- TmdbId 标记 {tmdb-NNN}（Plex 强制匹配） ----------

    [Fact]
    public void MovieFolder_WithTmdbId_AppendsMarker()
    {
        PlexNamingConventions.MovieFolder("盗梦空间", 2010, 27205).Should().Be("盗梦空间 (2010) {tmdb-27205}");
    }

    [Fact]
    public void MovieFileBase_WithTmdbId_AppendsMarkerAtEnd()
    {
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010, tmdbId: 27205)
            .Should().Be("盗梦空间 (2010) {tmdb-27205}");
    }

    [Fact]
    public void MovieFileBase_EditionAndTmdbId_MarkerAfterEdition()
    {
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010, "Director's Cut", 27205)
            .Should().Be("盗梦空间 (2010) - Director's Cut {tmdb-27205}");
    }

    [Fact]
    public void MovieFilePath_WithTmdbId_BothFolderAndFileMarked()
    {
        string p = PlexNamingConventions.MovieFilePath("盗梦空间", 2010, "mkv", tmdbId: 27205);
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"盗梦空间 (2010) {{tmdb-27205}}{s}盗梦空间 (2010) {{tmdb-27205}}.mkv");
    }

    [Fact]
    public void TvShowFolder_WithTmdbId_AppendsMarker()
    {
        PlexNamingConventions.TvShowFolder("绝命毒师", 2008, 1396).Should().Be("绝命毒师 (2008) {tmdb-1396}");
    }

    [Fact]
    public void TvEpisodeFilePath_WithTmdbId_OnlyShowFolderMarked()
    {
        string p = PlexNamingConventions.TvEpisodeFilePath("绝命毒师", 2008, 1, 1, "mkv", tmdbId: 1396);
        char s = Path.DirectorySeparatorChar;
        // 标记仅落剧集根目录；季目录与单集文件名保持干净
        p.Should().Be($"绝命毒师 (2008) {{tmdb-1396}}{s}Season 01{s}绝命毒师 (2008) - S01E01.mkv");
    }

    [Fact]
    public void TvShowNfoPath_WithTmdbId_LandsInMarkedFolder()
    {
        string p = PlexNamingConventions.TvShowNfoPath("绝命毒师", 2008, 1396);
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"绝命毒师 (2008) {{tmdb-1396}}{s}tvshow.nfo");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MovieFolder_NonPositiveTmdbId_NoMarker(int tmdbId)
    {
        PlexNamingConventions.MovieFolder("盗梦空间", 2010, tmdbId).Should().Be("盗梦空间 (2010)");
    }

    [Fact]
    public void MovieNfoPath_MatchesFileBase()
    {
        string p = PlexNamingConventions.MovieNfoPath("盗梦空间", 2010);
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"盗梦空间 (2010){s}盗梦空间 (2010).nfo");
    }

    [Fact]
    public void MovieNfoPath_WithEdition_IncludesEditionSuffix()
    {
        string p = PlexNamingConventions.MovieNfoPath("盗梦空间", 2010, "Director's Cut");
        p.Should().EndWith("盗梦空间 (2010) - Director's Cut.nfo");
    }

    [Fact]
    public void TvShowNfoPath_IsTvshowDotNfoAtRoot()
    {
        string p = PlexNamingConventions.TvShowNfoPath("绝命毒师", 2008);
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"绝命毒师 (2008){s}tvshow.nfo");
    }

    [Fact]
    public void Posters_MovieAndTv_FixedNamesAtRoot()
    {
        char s = Path.DirectorySeparatorChar;
        PlexNamingConventions.MoviePosterPath("盗梦空间", 2010).Should().Be($"盗梦空间 (2010){s}poster.jpg");
        PlexNamingConventions.MovieFanartPath("盗梦空间", 2010).Should().Be($"盗梦空间 (2010){s}fanart.jpg");
        PlexNamingConventions.TvShowPosterPath("绝命毒师", 2008).Should().Be($"绝命毒师 (2008){s}poster.jpg");
        PlexNamingConventions.SeasonPosterPath("绝命毒师", 2008, 1).Should().Be($"绝命毒师 (2008){s}season01-poster.jpg");
    }

    [Theory]
    [InlineData("a<b>c", "a_b_c")]
    [InlineData("a:b\"c", "a_b_c")]
    [InlineData("a/b\\c|d", "a_b_c_d")]
    [InlineData("a?b*c", "a_b_c")]
    [InlineData("hello.", "hello", "末尾点 trim（Windows 拒绝以点结尾）")]
    [InlineData("hello   world", "hello world", "多空格折叠")]
    public void Sanitize_StripsIllegalChars_AndTrims(string raw, string expected, string? _ = null)
    {
        PlexNamingConventions.Sanitize(raw).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_AllIllegal_ReplacedWithUnderscores()
    {
        // 标题全是非法字符 → 替换后变下划线串；不再抛异常（Plex 也能存）
        PlexNamingConventions.Sanitize("<>:").Should().Be("___");
    }

    [Fact]
    public void Sanitize_WhitespaceOnly_Throws()
    {
        Action act = () => PlexNamingConventions.Sanitize("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MovieFolder_WithIllegalTitleChars_Cleaned()
    {
        PlexNamingConventions.MovieFolder("Title: Bad/Name?", 2010)
            .Should().Be("Title_ Bad_Name_ (2010)");
    }

    [Fact]
    public void Year_OutOfRange_Throws()
    {
        Action lo = () => PlexNamingConventions.MovieFolder("x", 1500);
        Action hi = () => PlexNamingConventions.MovieFolder("x", 3000);
        lo.Should().Throw<ArgumentOutOfRangeException>();
        hi.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Episode_OutOfRange_Throws()
    {
        // 集号 0 现为合法（前导集 / 第 0 话），下界改判负数；上界 9999 不变
        Action neg = () => PlexNamingConventions.TvEpisodeFileBase("x", 2010, 1, -1);
        Action huge = () => PlexNamingConventions.TvEpisodeFileBase("x", 2010, 1, 10000);
        neg.Should().Throw<ArgumentOutOfRangeException>();
        huge.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TvEpisodeFileBase_Episode0_FormatsAsE00()
    {
        // 动漫前导集 / 第 0 话：episode=0 合法，生成 SxxE00（不再误判处理失败）
        PlexNamingConventions.TvEpisodeFileBase("某番", 2016, 1, 0)
            .Should().Be("某番 (2016) - S01E00");
    }

    [Fact]
    public void TvEpisodeFilePath_Episode0_LandsInS01E00()
    {
        // 端到端：第 0 集正常归档落 Season 01 / S01E00.mkv
        string p = PlexNamingConventions.TvEpisodeFilePath("某番", 2016, 1, 0, "mkv");
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"某番 (2016){s}Season 01{s}某番 (2016) - S01E00.mkv");
    }

    [Fact]
    public void NormalizeExtension_CasesAndDots()
    {
        PlexNamingConventions.NormalizeExtension("MKV").Should().Be("mkv");
        PlexNamingConventions.NormalizeExtension(".JPG").Should().Be("jpg");
        PlexNamingConventions.NormalizeExtension("  .NFO  ").Should().Be("nfo");
    }

    // ---------- 多季：季文件夹季标题 + 季内文件名季年份（方向 A） ----------

    [Fact]
    public void SeasonFolder_WithSeasonTitle_AppendsAsSuffix()
    {
        PlexNamingConventions.SeasonFolder(3, "锻刀村篇").Should().Be("Season 03 锻刀村篇");
    }

    [Fact]
    public void SeasonFolder_BlankSeasonTitle_FallsBackToPlain()
    {
        PlexNamingConventions.SeasonFolder(3, null).Should().Be("Season 03");
        PlexNamingConventions.SeasonFolder(3, "   ").Should().Be("Season 03");
    }

    [Fact]
    public void SeasonFolder_IllegalCharsInSeasonTitle_Cleaned()
    {
        PlexNamingConventions.SeasonFolder(2, "幽灵/子弹:篇").Should().Be("Season 02 幽灵_子弹_篇");
    }

    [Fact]
    public void TvEpisodeFilePath_SeasonYear_UsedInEpisodeFileName_RootKeepsShowYear()
    {
        // 根目录用整剧首播年 2019；季文件夹带季标题；季内单集文件名用该季首播年 2023
        string p = PlexNamingConventions.TvEpisodeFilePath(
            "鬼灭之刃", 2019, 3, 1, "mkv", tmdbId: 85937, seasonYear: 2023, seasonTitle: "锻刀村篇");
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"鬼灭之刃 (2019) {{tmdb-85937}}{s}Season 03 锻刀村篇{s}鬼灭之刃 (2023) - S03E01.mkv");
    }

    [Fact]
    public void TvEpisodeFilePath_NoSeasonInfo_DegradesToShowYearAndPlainSeason()
    {
        // 省略 seasonYear/seasonTitle → 与改造前完全一致（年份全用整剧年、季目录纯 Season NN）
        string p = PlexNamingConventions.TvEpisodeFilePath("绝命毒师", 2008, 1, 1, "mkv");
        char s = Path.DirectorySeparatorChar;
        p.Should().Be($"绝命毒师 (2008){s}Season 01{s}绝命毒师 (2008) - S01E01.mkv");
    }

    [Theory]
    [InlineData("锻刀村篇", null, "锻刀村篇", "TMDB 篇章季名直接采用")]
    [InlineData("Season 3", "幽灵子弹篇", "幽灵子弹篇", "TMDB 默认季名被过滤，回退解析篇章名")]
    [InlineData("第 3 季", "锻刀村篇", "锻刀村篇", "中文默认季名过滤")]
    [InlineData("Specials", null, null, "特别篇默认名过滤且无回退 → null")]
    [InlineData(null, "幽灵子弹篇", "幽灵子弹篇", "TMDB 季名缺失回退解析篇章名")]
    [InlineData(null, null, null, "两者皆无 → null")]
    [InlineData("Season 3", null, null, "默认季名过滤且无回退 → null")]
    public void NormalizeSeasonTitle_PrefersMeaningfulTmdbName_ElseParsed(string? tmdbName, string? parsed, string? expected, string _)
    {
        PlexNamingConventions.NormalizeSeasonTitle(tmdbName, parsed).Should().Be(expected);
    }

    // ---------- Default 模板重载逐字节回归（模板化改造后的核心护栏） ----------
    // 关键不变量：带 NamingTemplateOptions.Default 的新重载渲染结果必须与改造前硬编码完全一致，
    // 否则升级即破坏所有存量库的 {tmdb-NNN} 目录复用与 Plex 刮削。下列断言期望值与上方硬编码版逐字相同。

    private static readonly NamingTemplateOptions Default = NamingTemplateOptions.Default;

    [Fact]
    public void MovieFolder_DefaultOptionsOverload_ByteIdenticalToHardcoded()
    {
        PlexNamingConventions.MovieFolder("盗梦空间", 2010, null, Default, null).Should().Be("盗梦空间 (2010)");
        PlexNamingConventions.MovieFolder("盗梦空间", 2010, 27205, Default, null).Should().Be("盗梦空间 (2010) {tmdb-27205}");
    }

    [Fact]
    public void MovieFileBase_DefaultOptionsOverload_ByteIdenticalToHardcoded()
    {
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010, null, null, Default, null).Should().Be("盗梦空间 (2010)");
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010, "Director's Cut", 27205, Default, null)
            .Should().Be("盗梦空间 (2010) - Director's Cut {tmdb-27205}");
    }

    [Fact]
    public void TvShowFolder_DefaultOptionsOverload_ByteIdenticalToHardcoded()
    {
        PlexNamingConventions.TvShowFolder("绝命毒师", 2008, 1396, Default, null).Should().Be("绝命毒师 (2008) {tmdb-1396}");
    }

    [Fact]
    public void TvEpisodeFileBase_DefaultOptionsOverload_ByteIdenticalToHardcoded()
    {
        PlexNamingConventions.TvEpisodeFileBase("绝命毒师", 2008, 1, 1, null, Default, null)
            .Should().Be("绝命毒师 (2008) - S01E01");
        PlexNamingConventions.TvEpisodeFileBase("某番", 2016, 1, 0, null, Default, null)
            .Should().Be("某番 (2016) - S01E00");
        PlexNamingConventions.TvEpisodeFileBase("Show", 2020, 1, 8, 9, Default, null)
            .Should().Be("Show (2020) - S01E08-E09");
    }

    [Fact]
    public void MovieFileBase_DefaultTemplateNoEditionToken_OriginalTitleUnusedStaysIdentical()
    {
        // 默认模板不引用 {originaltitle}，即便传入原文名也不应改变输出（仅在自定义模板里才生效）
        PlexNamingConventions.MovieFileBase("盗梦空间", 2010, null, 27205, Default, "Inception")
            .Should().Be("盗梦空间 (2010) {tmdb-27205}");
    }

    [Fact]
    public void FormatEpisodeToken_LocksSxxEyy()
    {
        PlexNamingConventions.FormatEpisodeToken(1, 1, null).Should().Be("S01E01");
        PlexNamingConventions.FormatEpisodeToken(12, 99, null).Should().Be("S12E99");
        PlexNamingConventions.FormatEpisodeToken(1, 8, 9).Should().Be("S01E08-E09");
        PlexNamingConventions.FormatEpisodeToken(1, 8, 8).Should().Be("S01E08", "终集==起集退化单集");
        PlexNamingConventions.FormatEpisodeToken(0, 5, null).Should().Be("S00E05", "S00 特别篇");
    }
}
