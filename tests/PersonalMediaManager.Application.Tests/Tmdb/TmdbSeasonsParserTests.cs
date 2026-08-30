using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Tests.Tmdb;

/// <summary>TmdbSeasonsParser 单元测试：从 TMDB 详情 JSON 解析逐季集数</summary>
/// <remarks>支撑「绝对集号 → 季/集」换算；覆盖正常、特别篇季 0、缺字段、损坏 JSON、电影无 seasons 等分支。</remarks>
public sealed class TmdbSeasonsParserTests
{
    [Fact]
    public void Parse_Standard_Seasons_Returns_All_With_EpisodeCounts()
    {
        const string json = """
        {
          "name": "间谍过家家",
          "number_of_seasons": 2,
          "seasons": [
            { "season_number": 0, "episode_count": 5,  "name": "特别篇" },
            { "season_number": 1, "episode_count": 25, "name": "第 1 季" },
            { "season_number": 2, "episode_count": 12, "name": "第 2 季" }
          ]
        }
        """;

        IReadOnlyList<TmdbSeasonInfo> seasons = TmdbSeasonsParser.Parse(json);

        seasons.Select(s => (s.SeasonNumber, s.EpisodeCount))
            .Should().Equal((0, 5), (1, 25), (2, 12));
    }

    [Fact]
    public void Parse_Extracts_Season_Name()
    {
        // 季名（zh-CN）供审核页与篇章标题对照；篇章型番剧季名即「锻刀村篇」等
        const string json = """
        {
          "seasons": [
            { "season_number": 1, "episode_count": 12, "name": "第 1 季" },
            { "season_number": 3, "episode_count": 11, "name": "锻刀村篇" }
          ]
        }
        """;

        TmdbSeasonsParser.Parse(json).Select(s => s.Name).Should().Equal("第 1 季", "锻刀村篇");
    }

    [Fact]
    public void Parse_Extracts_Season_Year_From_AirDate()
    {
        // 季首播年来自 seasons[].air_date，供归档季内文件按该季年份命名（而非整剧首播年）
        const string json = """
        {
          "seasons": [
            { "season_number": 1, "episode_count": 12, "name": "第 1 季", "air_date": "2019-04-06" },
            { "season_number": 3, "episode_count": 11, "name": "锻刀村篇", "air_date": "2023-04-09" }
          ]
        }
        """;

        TmdbSeasonsParser.Parse(json).Select(s => s.Year).Should().Equal(new int?[] { 2019, 2023 });
    }

    [Fact]
    public void Parse_Missing_Or_Empty_AirDate_Year_Is_Null()
    {
        // 未播季 air_date 常为空串，缺字段同理 → Year 为 null（归档回退整剧首播年）
        const string json = """
        {
          "seasons": [
            { "season_number": 1, "episode_count": 12, "air_date": "" },
            { "season_number": 2, "episode_count": 0 }
          ]
        }
        """;

        TmdbSeasonsParser.Parse(json).Should().OnlyContain(s => s.Year == null);
    }

    [Fact]
    public void Parse_Missing_Name_Is_Null()
    {
        const string json = """{ "seasons": [ { "season_number": 1, "episode_count": 12 } ] }""";
        TmdbSeasonsParser.Parse(json).Single().Name.Should().BeNull();
    }

    [Fact]
    public void Parse_Missing_EpisodeCount_Defaults_To_Zero()
    {
        const string json = """{ "seasons": [ { "season_number": 1 } ] }""";
        TmdbSeasonsParser.Parse(json).Single().EpisodeCount.Should().Be(0);
    }

    [Fact]
    public void Parse_Item_Without_SeasonNumber_Is_Skipped()
    {
        const string json = """{ "seasons": [ { "episode_count": 10 }, { "season_number": 1, "episode_count": 12 } ] }""";
        TmdbSeasonsParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be(new TmdbSeasonInfo(1, 12));
    }

    [Fact]
    public void Parse_No_Seasons_Field_Returns_Empty()
    {
        // 电影详情无 seasons 字段
        TmdbSeasonsParser.Parse("""{ "title": "盗梦空间", "release_date": "2010-07-16" }""").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{ "seasons": "notarray" }""")]
    public void Parse_Null_Blank_Or_Malformed_Returns_Empty(string? raw)
    {
        TmdbSeasonsParser.Parse(raw).Should().BeEmpty();
    }
}
