using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Theory]
    [InlineData("食戟之灵 餐之皿", 3, "餐之皿")]
    [InlineData("食戟之靈 神之皿", 4, "神之皿")]
    [InlineData("食戟のソーマ 豪ノ皿", 5, "豪ノ皿")]
    public async Task LicensedSeasonSubtitleKeepsMappingEvidence(string title, int season, string subtitle)
    {
        RuleParseResult result = await Parse($"[Example-Raws][{title}][09][720P][WEB-DL][AAC].mkv", null);
        result.Season.Should().Be(season);
        result.Episode.Should().Be(9);
        result.SeasonTitle.Should().Be(subtitle);
        result.FieldEvidence.Should().Contain(e => e.Source == "LicensedSeasonCatalogue" && e.Value == season);
        result.Year.Should().BeNull();
    }

    [Theory]
    [InlineData("另一部作品 餐之皿")]
    [InlineData("食戟之灵 皿之餐")]
    [InlineData("食戟之灵 餐之皿外传")]
    public async Task UnrecognizedSubtitleCannotInventSeason(string title)
    {
        (await Parse($"[Example-Raws][{title}][01].mkv", null)).Season.Should().BeNull();
    }

    [Fact]
    public async Task ExplicitSeasonAndSubtitleConflictIsPreserved()
    {
        RuleParseResult result = await Parse("[Example-Raws][食戟之灵 餐之皿][01].mkv", "Season 2");
        result.Season.Should().Be(2);
        result.Conflicts.Should().NotBeEmpty();
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Fact]
    public async Task ConfirmedNumericSeasonHasIndependentEpisode()
    {
        RuleParseResult result = await Parse("[Example-Subs] GRAND BLUE 碧藍之海 3 - 01 [720P][WEB-DL][AAC AVC][CHT].mp4", null);
        result.Season.Should().Be(3);
        result.Episode.Should().Be(1);
        result.Title.Should().Be("碧藍之海");
        result.NamingEvidence!.SeasonMappingSource.Should().Be("ConfirmedSeriesNumbering");
        result.AlternativeTitles.Should().Contain("GRAND BLUE");
    }

    [Theory]
    [InlineData("Example.S02 - 01 [1080P].mkv")]
    [InlineData("Example Season 2 - 01.mkv")]
    public async Task DashEpisodeKeepsAlreadyCleanedExplicitSeasonTitle(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be("Example");
        result.Season.Should().Be(2);
        result.Episode.Should().Be(1);
    }

    [Theory]
    [InlineData("Toy Story 3 - 01.mkv", "Toy Story 3")]
    [InlineData("Example Arc 3 - 01.mkv", "Example Arc 3")]
    [InlineData("[Example-Subs] Another Series 3 - 01 [1080P].mkv", "Another Series 3")]
    public async Task AmbiguousSeasonOrSequelIsNotSilentlyRemoved(string file, string title)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be(title);
        result.Season.Should().BeNull();
        result.NamingEvidence!.SeasonCandidate.Should().Be(3);
        result.NamingEvidence.Uncertainties.Should().Contain("SeasonOrSequelNumber");
    }

    [Theory]
    [InlineData("Season 3")]
    [InlineData("S03")]
    public async Task NumberedTitleCanUseIndependentExplicitSeason(string parent)
    {
        RuleParseResult result = await Parse("Example 3 - 01.mkv", parent);
        result.Title.Should().Be("Example");
        result.Season.Should().Be(3);
        result.FieldEvidence.Should().Contain(e => e.Source == "ExplicitSeasonAgreement");
    }

    [Fact]
    public async Task RomanSeriesNameCannotBecomeAnUnprovenSeason()
    {
        RuleParseResult result = await Parse("Lupin III - 01.mkv", null);
        result.Title.Should().Be("Lupin III");
        result.Season.Should().BeNull();
        result.NamingEvidence!.SeasonCandidate.Should().Be(3);
    }

    [Theory]
    [InlineData("[Example-Subs] GRAND BLUE 碧藍之海 3 - 01.5 [1080P].mp4")]
    [InlineData("[Example-Subs] GRAND BLUE 碧藍之海 3 - 1080P.mp4")]
    public async Task FractionalAndTechnicalTailsAreNotConfirmedEpisodes(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Episode.Should().BeNull();
        result.Season.Should().BeNull();
    }

    [Fact]
    public async Task RemasterPreservesFullTitleAndOffersVersionlessCandidate()
    {
        RuleParseResult result = await Parse("[Example-Raws][Example Series HD Remaster][03][720P][WEB-DL][AAC].mkv", null);
        result.Title.Should().Be("Example Series HD Remaster");
        result.AlternativeTitles.Should().Contain("Example Series");
        result.NamingEvidence!.EditionTags.Should().Contain("HD Remaster");
        result.Season.Should().BeNull();
    }

    [Theory]
    [InlineData("Extended Edition")]
    [InlineData("Directors Cut")]
    [InlineData("HD Remaster")]
    public async Task UnbracketedEditionSurvivesTechnicalCleaning(string edition)
    {
        RuleParseResult result = await Parse($"Example {edition}.2024.1080p.mkv", null);
        result.Title.Should().Be($"Example {edition}");
        result.NamingEvidence!.OriginalTitle.Should().Be($"Example {edition}");
        result.NamingEvidence.EditionTags.Should().Contain(edition);
        result.AlternativeTitles.Should().Contain("Example");
    }

    [Fact]
    public async Task SpecialEditionCannotBeAssumedTvFromBracketNumber()
    {
        RuleParseResult result = await Parse("[Example-Raws][Example Series HD Remaster][Special Edition][03][Final Chapter][720P][WEB-DL][AAC].mkv", null);
        result.Title.Should().Be("Example Series HD Remaster");
        result.MediaType.Should().Be("unknown");
        result.NamingEvidence!.EditionTags.Should().Contain("Special Edition");
    }

    [Theory]
    [InlineData("The Godfather II.1974.1080p.mkv", "The Godfather II")]
    [InlineData("Rocky III.1982.mkv", "Rocky III")]
    public async Task RomanMovieSequelWithoutEpisodeCannotBecomeSeason(string file, string title)
    {
        RuleParseResult result = await Parse(file, null);
        result.Title.Should().Be(title);
        result.Season.Should().BeNull();
        result.MediaType.Should().Be("movie");
    }

    [Theory]
    [InlineData("News.2026.10.02.E03.mkv")]
    [InlineData("News.20261002.E03.mkv")]
    [InlineData("Example.1920x1080.mkv")]
    [InlineData("Example.2048p.mkv")]
    public async Task DateOrResolutionDoesNotSupplyWorkYear(string file)
    {
        (await Parse(file, null)).Year.Should().BeNull();
    }

    [Fact]
    public async Task DateDirectoriesDoNotLeaveAnInferredMovieType()
    {
        RuleParseResult result = await _sut.ParseAsync(new("Example.mkv", "Example.mkv", null, ["2026", "10", "02"]));
        result.Year.Should().BeNull();
        result.MediaType.Should().Be("unknown");
        result.Confidence.Should().BeLessThan(0.6);
    }

    [Fact]
    public async Task UserForceMovieProtectsNumericSequel()
    {
        SeedRule(new ParseRule { Name = "movie", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^(?<title>Toy Story 3)", ForceType = true, DefaultType = "movie" });
        RuleParseResult result = await Parse("Toy Story 3 - 01.mkv", null);
        result.Title.Should().Be("Toy Story 3");
        result.MediaType.Should().Be("movie");
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }
}
