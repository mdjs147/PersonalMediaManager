using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Fact]
    public async Task FileEpisodeTitle_SyntheticBareNumberRemainsUnprovenEpisodeCandidate()
    {
        const string file = "Example Chronicle 21 [WEB-DL 1280x720 AVC x264 8bit ASSx1].mkv";
        RuleParseResult result = await Parse(file, "Example Chronicle [WEB-DL 1280x720 AVC x264 8bit]");
        result.Title.Should().Be("Example Chronicle 21");
        result.AlternativeTitles.Should().Contain("Example Chronicle");
        result.Episode.Should().BeNull();
        result.Season.Should().BeNull();
        result.Year.Should().BeNull();
        result.MediaType.Should().NotBe("tv");
        result.NamingEvidence!.Uncertainties.Should().Contain("EpisodeOrSequelNumber");
        RuleSourceNumberCandidate candidate = result.NamingEvidence.NumberingCandidate!;
        candidate.Value.Should().Be(21);
        candidate.Source.Should().Be("FileName");
        file.Substring(candidate.Start, candidate.Length).Should().Be(candidate.Token).And.Be("21");
    }

    [Theory]
    [InlineData("Toy Story", 3)]
    [InlineData("Other Programme", 21)]
    public async Task FileEpisodeTitle_ParentAgreementDoesNotTurnSequelIntoEpisode(string title, int number)
    {
        RuleParseResult result = await Parse($"{title} {number} [1080p].mkv", $"{title} [1080p]");
        result.Title.Should().Be($"{title} {number}");
        result.Episode.Should().BeNull();
        result.MediaType.Should().NotBe("tv");
        result.NamingEvidence!.NumberingCandidate!.Value.Should().Be(number);
        result.NamingEvidence.Uncertainties.Should().Contain("EpisodeOrSequelNumber");
    }

    [Theory]
    [InlineData("Other Programme 21 [1080p].mkv", "Unrelated [1080p]")]
    [InlineData("Other Programme 21 [1080p Finale].mkv", "Other Programme [1080p]")]
    [InlineData("Other Programme 21.5 [1080p].mkv", "Other Programme [1080p]")]
    [InlineData("Other Programme 1917 [1080p].mkv", "Other Programme [1080p]")]
    [InlineData("Other Programme 1984 [1080p].mkv", "Other Programme [1080p]")]
    public async Task FileEpisodeTitle_BareNumberCandidateNeedsExactParentAndTechnicalTail(string file, string folder)
    {
        RuleParseResult result = await Parse(file, folder);
        result.NamingEvidence!.NumberingCandidate.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Fact]
    public async Task FileEpisodeTitle_SyntheticTechnicalParenthesesKeepNameAndEpisode()
    {
        RuleParseResult result = await Parse("[Example-Subs] Example Chronicle - 01 (CR 1920x1080 AVC AAC MKV) [0A1B2C3D].mkv", "迅雷下载");
        result.Title.Should().Be("Example Chronicle");
        result.Episode.Should().Be(1);
        result.EpisodeEnd.Should().BeNull();
        result.Season.Should().BeNull();
        result.Year.Should().BeNull();
        result.FieldEvidence.Should().Contain(e => e.Field == "episode" && e.Value == 1 && e.Source == "FileName");
    }

    [Theory]
    [InlineData("[Example Group] Example Name - 02 (1080p HEVC AAC MKV) [12ABCDEF].mkv", "Example Name", 2)]
    [InlineData("Different Programme - 03 (BD 1920x1080 x265 10bit ASSx2).mkv", "Different Programme", 3)]
    [InlineData("Toy Story 3 - 01 (CR 1080p AVC AAC MKV).mkv", "Toy Story 3", 1)]
    public async Task FileEpisodeTitle_TechnicalParenthesesAreGeneric(string file, string title, int episode)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be(title);
        result.Episode.Should().Be(episode);
        result.Season.Should().BeNull();
        result.Year.Should().BeNull();
    }

    [Theory]
    [InlineData("Example - 01 (Flames of Destiny).mkv")]
    [InlineData("Example - 01 (1080p Finale).mkv")]
    [InlineData("Example - 01 (CR 1080p AAC MKV) [Side Story].mkv")]
    [InlineData("Example - 01.5 (1080p HEVC AAC).mkv")]
    [InlineData("Example - 01-02 (1080p HEVC AAC).mkv")]
    [InlineData("Example - 1917 (1080p HEVC AAC).mkv")]
    public async Task FileEpisodeTitle_UnknownContentFractionAndRangeDoNotBecomeSingleEpisode(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Episode.Should().BeNull();
        result.EpisodeEnd.Should().BeNull();
    }

    [Fact]
    public async Task FileEpisodeTitle_ForceMovieRemainsAuthoritative()
    {
        SeedRule(new ParseRule { Name = "保留电影", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^(?<title>Example)", ForceType = true, DefaultType = "movie" });
        RuleParseResult result = await Parse("Example - 01 (1080p HEVC AAC).mkv", null);
        result.MediaType.Should().Be("movie");
        result.Episode.Should().BeNull();
    }
}
