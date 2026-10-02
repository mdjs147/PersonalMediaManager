using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Fact]
    public async Task FolderRangeTitle_SyntheticInputKeepsCurrentFileSingleEpisode()
    {
        RuleParseResult result = await Parse("21.720p.HD粤语中字无水印[最新电影www.example.test].mkv", "测试剧名21-22.720p");

        result.Title.Should().Be("测试剧名");
        result.MediaType.Should().Be("tv");
        result.Episode.Should().Be(21);
        result.EpisodeEnd.Should().BeNull();
        result.Season.Should().BeNull();
        result.Year.Should().BeNull();
        result.HasIdentityEvidence.Should().BeTrue();
        result.FieldEvidence.Should().Contain(e => e.Field == "episode" && e.Value == 21 && e.Source == "FileName");
        result.FieldEvidence.Should().NotContain(e => e.Field == "episodeEnd");
        result.AlternativeTitles.Should().NotContain(t => t.Contains("21") || t.Contains("22"));
    }

    [Theory]
    [InlineData("21.mkv", "测试剧名21-22", "测试剧名", 21)]
    [InlineData("22.2160p.mkv", "测试剧名21–22.2160p", "测试剧名", 22)]
    [InlineData("E21.mkv", "Example Programme 21~22.1080p", "Example Programme", 21)]
    [InlineData("[21].mkv", "Example Programme_021-022_[1080p]", "Example Programme", 21)]
    [InlineData("21.mkv", "Toy Story 3 21-22.2160p", "Toy Story 3", 21)]
    [InlineData("21.mkv", "战狼2 21-22.2160p", "战狼2", 21)]
    public async Task FolderRangeTitle_UsesRangeBoundaryWithoutRemovingSequel(string file, string folder, string title, int episode)
    {
        RuleParseResult result = await Parse(file, folder);
        result.Title.Should().Be(title);
        result.Episode.Should().Be(episode);
        result.EpisodeEnd.Should().BeNull();
        result.Season.Should().BeNull();
    }

    [Theory]
    [InlineData("media.mkv")]
    [InlineData("1080p.mkv")]
    [InlineData("21.5.mkv")]
    [InlineData("E21.5.mkv")]
    [InlineData("21-22.mkv")]
    public async Task FolderRangeTitle_WithoutIndependentFileEpisodeKeepsUnprovenNumbers(string file)
    {
        RuleParseResult result = await Parse(file, "测试剧名21-22.2160p");
        result.Episode.Should().BeNull();
        result.EpisodeEnd.Should().BeNull();
        result.Title.Should().NotBe("测试剧名");
        result.AlternativeTitles.Should().NotContain("测试剧名");
    }

    [Theory]
    [InlineData("测试剧名22-21.2160p")]
    [InlineData("测试剧名21-21.2160p")]
    [InlineData("测试剧名01-20.2160p")]
    [InlineData("测试剧名00-22.2160p")]
    [InlineData("测试剧名1021-1022.2160p")]
    [InlineData("测试剧名21-22号公路")]
    [InlineData("测试剧名21-22 Finale")]
    [InlineData("测试剧名21 22.2160p")]
    public async Task FolderRangeTitle_RejectsUncorroboratedOrAmbiguousRange(string folder)
    {
        RuleParseResult result = await Parse("21.mkv", folder);
        result.Title.Should().NotBe("测试剧名");
        result.AlternativeTitles.Should().NotContain("测试剧名");
        result.Episode.Should().Be(21);
        result.EpisodeEnd.Should().BeNull();
    }

    [Fact]
    public async Task FolderRangeTitle_AncestorEpisodeIsNotIndependentFileEvidence()
    {
        RuleParseResult result = await _sut.ParseAsync(new("1080p.mkv", "1080p.mkv", null,
            ["测试剧名21-22.2160p", "E21"]));
        result.Episode.Should().Be(21);
        result.Title.Should().Be("测试剧名21 22");
        result.AlternativeTitles.Should().NotContain("测试剧名");
    }

    [Fact]
    public async Task FolderRangeTitle_DirectoryCandidateUsesSameBoundaryAsPrimaryTitle()
    {
        RuleParseResult result = await Parse("Another Name.S01E21.mkv", "测试剧名21-22.2160p");
        result.Title.Should().Be("Another Name");
        result.AlternativeTitles.Should().Contain("测试剧名");
        result.AlternativeTitles.Should().NotContain("测试剧名21 22");
        result.EpisodeEnd.Should().BeNull();
    }

    [Fact]
    public async Task FolderRangeTitle_FileRangeRemainsAFileRange()
    {
        RuleParseResult result = await Parse("Example.S01E21-E22.mkv", "测试剧名21-22.2160p");
        result.Title.Should().Be("Example");
        result.Episode.Should().Be(21);
        result.EpisodeEnd.Should().Be(22);
    }

    [Fact]
    public async Task FolderRangeTitle_InvalidFileRangeCannotCorroborateDirectoryTitle()
    {
        RuleParseResult result = await Parse("S01E22-E21.mkv", "测试剧名21-22.2160p");
        result.Title.Should().Be("测试剧名21 22");
        result.AlternativeTitles.Should().NotContain("测试剧名");
        result.Episode.Should().BeNull();
        result.EpisodeEnd.Should().BeNull();
        result.RejectedFields.Should().Contain("episode");
    }

    [Theory]
    [InlineData("1917")]
    [InlineData("1984")]
    [InlineData("Toy Story 3")]
    [InlineData("战狼2")]
    [InlineData("测试剧名21-22")]
    public async Task FolderRangeTitle_DoesNotStripNumbersFromFileIdentity(string title)
    {
        RuleParseResult result = await Parse(title + ".mkv", null);
        result.Title.Should().Be(title.Replace('-', ' '));
        result.Episode.Should().BeNull();
        result.EpisodeEnd.Should().BeNull();
        result.Season.Should().BeNull();
    }

    [Fact]
    public async Task FolderRangeTitle_DoesNotOverwriteUserTitleOrForceMovie()
    {
        SeedRule(new ParseRule { Name = "保留用户标题", Enabled = true, Priority = 1, Scope = ParseScope.ParentFolder,
            Pattern = @"^(?<title>测试剧名21-22)", ForceType = true, DefaultType = "movie" });
        RuleParseResult result = await Parse("21.mkv", "测试剧名21-22.2160p");
        result.Title.Should().Be("测试剧名21 22");
        result.MediaType.Should().Be("movie");
        result.Episode.Should().BeNull();
        result.EpisodeEnd.Should().BeNull();
        result.AlternativeTitles.Should().NotContain("测试剧名");
    }
}
