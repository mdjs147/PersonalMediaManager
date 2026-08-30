using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

/// <summary>ForcedMatchMarkerParser — pmm.txt / TMDB URL → ForcedMatchMarker 解析</summary>
public sealed class ForcedMatchMarkerParserTests
{
    [Fact(DisplayName = "剧集组 URL：拆出 tmdb + 类型 + 剧集组 + 分组")]
    public void EpisodeGroupUrl_Parsed()
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse(
            "https://www.themoviedb.org/tv/20111-seed/episode_group/5deddbdcdc86470011c4bb7d/group/5deddbfedaf57c0015ed4e0a");

        m.Should().NotBeNull();
        m!.TmdbId.Should().Be(20111);
        m.MediaType.Should().Be("tv");
        m.EpisodeGroupId.Should().Be("5deddbdcdc86470011c4bb7d");
        m.GroupId.Should().Be("5deddbfedaf57c0015ed4e0a");
        m.Season.Should().BeNull();
    }

    [Fact(DisplayName = "季 URL：拆出 tmdb + 季号")]
    public void SeasonUrl_Parsed()
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse("https://www.themoviedb.org/tv/20111-seed/season/1");
        m!.TmdbId.Should().Be(20111);
        m.MediaType.Should().Be("tv");
        m.Season.Should().Be(1);
        m.EpisodeGroupId.Should().BeNull();
    }

    [Fact(DisplayName = "纯剧集 URL：仅锚 series")]
    public void PlainTvUrl_Parsed()
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse("https://www.themoviedb.org/tv/20111-seed");
        m!.TmdbId.Should().Be(20111);
        m.MediaType.Should().Be("tv");
        m.Season.Should().BeNull();
        m.EpisodeGroupId.Should().BeNull();
    }

    [Fact(DisplayName = "电影 URL：类型为 movie")]
    public void MovieUrl_Parsed()
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse("https://www.themoviedb.org/movie/27205-inception");
        m!.TmdbId.Should().Be(27205);
        m.MediaType.Should().Be("movie");
    }

    [Fact(DisplayName = "key=value 全字段")]
    public void KeyValue_AllFields()
    {
        string content = """
            # 强制匹配
            tmdb = 20111
            type = tv
            season = 2
            episode_group = abc123
            group = def456
            title = 机动战士高达SEED
            """;
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse(content);
        m!.TmdbId.Should().Be(20111);
        m.MediaType.Should().Be("tv");
        m.Season.Should().Be(2);
        m.EpisodeGroupId.Should().Be("abc123");
        m.GroupId.Should().Be("def456");
        m.TitleOverride.Should().Be("机动战士高达SEED");
    }

    [Fact(DisplayName = "裸数字一行 → tmdb id，类型默认 tv")]
    public void BareNumber_AsTmdbId_DefaultTv()
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse("20111");
        m!.TmdbId.Should().Be(20111);
        m.MediaType.Should().Be("tv");
    }

    [Fact(DisplayName = "URL 行 + key=value 覆盖：显式 season 覆盖 URL 未带季")]
    public void UrlPlusOverride()
    {
        string content = """
            https://www.themoviedb.org/tv/20111-seed
            season = 3
            title = 自定义名
            """;
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse(content);
        m!.TmdbId.Should().Be(20111);
        m.Season.Should().Be(3);
        m.TitleOverride.Should().Be("自定义名");
    }

    [Fact(DisplayName = "注释 / 空行 / ; // 前缀全部忽略")]
    public void CommentsIgnored()
    {
        string content = """

            # 这是注释
            ; 这也是
            // 还是注释
            tmdb=12345
            """;
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse(content);
        m!.TmdbId.Should().Be(12345);
    }

    [Fact(DisplayName = ".url 快捷方式的 URL=... 行可解析")]
    public void DotUrlShortcutLine_Parsed()
    {
        string content = """
            [InternetShortcut]
            URL=https://www.themoviedb.org/tv/20111-seed/episode_group/aaa/group/bbb
            """;
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse(content);
        m!.TmdbId.Should().Be(20111);
        m.EpisodeGroupId.Should().Be("aaa");
        m.GroupId.Should().Be("bbb");
    }

    [Theory(DisplayName = "类型归一化：中英别名都识别")]
    [InlineData("电视剧", "tv")]
    [InlineData("剧集", "tv")]
    [InlineData("series", "tv")]
    [InlineData("电影", "movie")]
    [InlineData("film", "movie")]
    public void TypeNormalization(string raw, string expected)
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.Parse($"tmdb=1\ntype={raw}");
        m!.MediaType.Should().Be(expected);
    }

    [Theory(DisplayName = "无有效 tmdb id → null（视为无效标识，走正常解析）")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# 只有注释")]
    [InlineData("type=tv\nseason=1")]
    [InlineData("tmdb=0")]
    [InlineData("随便写点什么")]
    public void InvalidOrEmpty_ReturnsNull(string content)
    {
        ForcedMatchMarkerParser.Parse(content).Should().BeNull();
    }

    // ---------- 文件夹名 / 文件名 {tmdb-NNN} 标记（仅锚 id，类型/季由规则识别）----------

    [Theory(DisplayName = "标记：文件夹名 / 文件名（含扩展名）的 {tmdb-NNN}/[tmdbid-]/大写/含空白 都识别出 id")]
    [InlineData("机动战士高达SEED (2002) {tmdb-20111}", 20111)]              // 归档侧产出的剧集根目录形态
    [InlineData("盗梦空间 (2010) {tmdb-27205}", 27205)]                       // 电影目录同款
    [InlineData("流浪地球 (2019) {tmdb-535292}.mkv", 535292)]                 // 文件名 + 扩展名（标记在扩展名前）
    [InlineData("Inception (2010) {tmdb-27205} - Director's Cut.mkv", 27205)] // 标记在文件名中段、扩展名前
    [InlineData("某剧 [tmdb-555]", 555)]                                     // 方括号变体
    [InlineData("某剧 [tmdbid-666]", 666)]                                   // tmdbid 键名变体
    [InlineData("某剧 {TMDB-777}", 777)]                                     // 大写
    [InlineData("某剧 { tmdb - 888 }", 888)]                                 // 容标记内空白
    public void Marker_ParsesIdOnly(string name, int expectedId)
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.ParseTmdbMarker(name);

        m.Should().NotBeNull();
        m!.TmdbId.Should().Be(expectedId);
        // 关键语义：只锚 id，类型 / 季 / 剧集组一律不锁，交由规则引擎正常识别
        m.MediaType.Should().BeNull();
        m.Season.Should().BeNull();
        m.EpisodeGroupId.Should().BeNull();
        m.GroupId.Should().BeNull();
        m.TitleOverride.Should().BeNull();
    }

    [Fact(DisplayName = "标记：同名多个标记取第一个（确定性）")]
    public void Marker_MultipleMarkers_TakesFirst()
    {
        ForcedMatchMarker? m = ForcedMatchMarkerParser.ParseTmdbMarker("混放 {tmdb-111} 又 {tmdb-222}");
        m!.TmdbId.Should().Be(111);
    }

    [Theory(DisplayName = "无标记 / id 无效 → null（退回规则正常识别）")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("机动战士高达SEED (2002)")]   // 普通名字，无标记
    [InlineData("第1季")]
    [InlineData("Season 01")]
    [InlineData("01.mkv")]                    // 普通文件名，无标记
    [InlineData("{tmdb-0}")]                  // id 为 0 非法
    [InlineData("tmdb-12345")]                // 缺括号不当标记（防误判普通文本）
    [InlineData("{tvdb-12345}")]              // tvdb 非 tmdb，不识别
    public void Marker_NoMarkerOrInvalid_ReturnsNull(string? name)
    {
        ForcedMatchMarkerParser.ParseTmdbMarker(name).Should().BeNull();
    }
}
