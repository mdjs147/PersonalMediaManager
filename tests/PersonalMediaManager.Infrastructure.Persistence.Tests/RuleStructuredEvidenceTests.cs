using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Setup;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    private async Task PrepareStructuredRules(int mode)
    {
        if (mode == 1)
            await new DataSeeder(_dbFactory, NullLogger<DataSeeder>.Instance).SeedAsync();
        if (mode == 2)
            SeedRule(new ParseRule { Name = "历史标题捕获", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
                Pattern = @"^(?<title>Example)", DefaultType = "tv", ConfidenceBonus = 1 });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StructuredCoordinatesAreSharedAcrossRuleConfigurations(int mode)
    {
        await PrepareStructuredRules(mode);
        foreach (string token in new[] { "S02 EP03", "[S02][EP03]", "2x03", "Season2 Episode3", "Ｓ０２Ｅ０３", "第貳季 第參話", "第二季 第十二集" })
        {
            string file = $"Example.{token}.1080p.mkv";
            RuleParseResult result = await Parse(file, null);
            result.Season.Should().Be(2, file);
            result.Episode.Should().Be(token.Contains("十二") ? 12 : 3, file);
            result.Conflicts.Should().BeNullOrEmpty(file);
            AssertStructuredSpans(result, FileParseContext.FileNameOnly(file));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FractionalNumberBlocksEveryParentReinjection(int mode)
    {
        await PrepareStructuredRules(mode);
        foreach (string token in new[] { "S01E11.5", "S01E11.5v2", "[11.5]", "11.5v2" })
        {
            string file = $"Example {token}.mkv";
            RuleParseResult result = await Parse(file, "Example S01E11");
            result.Episode.Should().BeNull(file);
            result.EpisodeEnd.Should().BeNull(file);
            result.RejectedFields.Should().Contain("episode");
            result.RejectedFields.Should().Contain("episodeEnd");
            result.Confidence.Should().BeLessThan(0.5);
            result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.FractionalEpisode
                && e.TextValue == "11.5" && e.State == RuleEvidenceState.Rejected);
            if (token.EndsWith("v2")) result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.ReleaseRevision && e.Value == 2);
        }
    }

    [Fact]
    public async Task IntegerRevisionPreservesEpisodeAndRevisionSource()
    {
        RuleParseResult result = await Parse("Example.S01E11v2.mkv", null);
        result.Episode.Should().Be(11);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.ReleaseRevision && e.Value == 2 && e.Token == "v2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StructuredConflictsIncludeSameLayerAndEveryAncestor(int mode)
    {
        await PrepareStructuredRules(mode);
        FileParseContext context = new("Example.S01E02.2024.mkv", "Example.S01E02.2024.mkv", null,
            ["Example Season 4 E06", "Example Season 2 E05"]);
        RuleParseResult result = await _sut.ParseAsync(context);
        result.Season.Should().Be(1);
        result.Episode.Should().Be(2);
        result.Conflicts.Should().Contain(c => c.StartsWith("season："));
        result.Conflicts.Should().Contain(c => c.StartsWith("episode："));
        result.Confidence.Should().BeLessThan(0.5);
        result.NumberingEvidence.Should().Contain(e => e.SegmentIndex == 0 && e.Value == 4 && e.State == RuleEvidenceState.Conflict);
        AssertStructuredSpans(result, context);
        RuleParseResult same = await Parse("Example.S01E02.S02E03.mkv", null);
        same.Season.Should().Be(1);
        same.Episode.Should().Be(2);
        same.Conflicts.Should().Contain(c => c.StartsWith("season："));
        same.Conflicts.Should().Contain(c => c.StartsWith("episode："));
    }

    [Fact]
    public async Task MatchingRepeatedFoldersKeepTheirActualIndexes()
    {
        FileParseContext context = new("Example.E03.mkv", "Example.E03.mkv", null, ["Season 2", "Season 2"]);
        RuleParseResult result = await _sut.ParseAsync(context);
        result.Season.Should().Be(2);
        result.Episode.Should().Be(3);
        result.Conflicts.Should().BeNullOrEmpty();
        result.NumberingEvidence!.Where(e => e.Kind == RuleNumberingKind.Season).Select(e => e.SegmentIndex)
            .Should().BeEquivalentTo(new int?[] { 0, 1 });
        AssertStructuredSpans(result, context);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DateVolumeAndAbsoluteCandidatesNeverBecomeLocalEpisode(int mode)
    {
        await PrepareStructuredRules(mode);
        foreach ((string token, RuleNumberingKind kind) in new[]
        {
            ("20250102", RuleNumberingKind.AirDate), ("2025-01-02", RuleNumberingKind.AirDate),
            ("250102", RuleNumberingKind.ShortAirDate), ("Vol.02", RuleNumberingKind.Volume),
            ("Disc 2", RuleNumberingKind.Disc), ("Part 2", RuleNumberingKind.Part),
            ("Cour 2", RuleNumberingKind.Cour), ("第貳期", RuleNumberingKind.Issue),
            ("#102", RuleNumberingKind.Absolute), ("No.102", RuleNumberingKind.Absolute),
        })
        {
            RuleParseResult result = await Parse($"Example {token} [1080p].mkv", "Season 1");
            result.Episode.Should().BeNull(token);
            result.EpisodeEnd.Should().BeNull(token);
            result.Year.Should().BeNull(token);
            result.Confidence.Should().BeLessThan(0.5, token);
            result.NumberingEvidence.Should().Contain(e => e.Kind == kind && e.State == RuleEvidenceState.Candidate);
        }
    }

    [Theory]
    [InlineData("20250102", @"^(?<title>Example)\.(?<year>20\d{2})(?<episode>\d{4})")]
    [InlineData("250102", @"^(?<title>Example)\.(?<episode>[12]\d{5})")]
    [InlineData("Vol.02", @"^(?<title>Example)\.Vol\.(?<episode>\d{2})")]
    [InlineData("#102", @"^(?<title>Example)\.#(?<episode>\d{3})")]
    public async Task RetainedUnsafeUserCapturesAreRejectedWithoutEditingTheirRules(string token, string pattern)
    {
        SeedRule(new ParseRule { Name = "历史编号捕获", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = pattern, DefaultType = "tv", ConfidenceBonus = 1 });
        RuleParseResult result = await Parse($"Example.{token}.mkv", "Season 1");
        result.Episode.Should().BeNull();
        result.Year.Should().BeNull();
        result.Confidence.Should().BeLessThan(0.5);
        using PmmDbContext db = _dbFactory.CreateDbContext();
        db.ParseRules.Single().Pattern.Should().Be(pattern);
    }

    [Theory]
    [InlineData("Vol.02")]
    [InlineData("20250102")]
    [InlineData("#102")]
    [InlineData("Part 2")]
    public async Task IndependentEpisodeCoexistsWithUnmappedSourceNumber(string token)
    {
        RuleParseResult result = await Parse($"Example.S02E03.{token}.mkv", null);
        result.Season.Should().Be(2);
        result.Episode.Should().Be(3);
        result.EpisodeEnd.Should().BeNull();
    }

    [Fact]
    public async Task OldVolumeCaptureCannotOverrideIndependentLocalEpisode()
    {
        SeedRule(new ParseRule { Name = "历史卷号", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^(?<title>Example) Vol\.(?<episode>\d{2})", DefaultType = "tv", ConfidenceBonus = 1 });
        RuleParseResult result = await Parse("Example Vol.02 S02E03.mkv", null);
        result.Season.Should().Be(2);
        result.Episode.Should().Be(3);
        result.FieldEvidence.Should().NotContain(e => e.Field == "episode" && e.Value == 2);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Volume && e.Value == 2);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Volume && e.Value == 2
            && e.Field == "episode" && e.State == RuleEvidenceState.Rejected && e.Token == "Vol.02");
    }

    [Fact]
    public async Task PartitionParentCannotCreateSeasonThroughLegacyCapture()
    {
        SeedRule(new ParseRule { Name = "历史放送期", Enabled = true, Priority = 1, Scope = ParseScope.ParentFolder,
            Pattern = @"^(?<title>Example) Cour (?<season>\d)", DefaultType = "tv" });
        RuleParseResult result = await Parse("03.mkv", "Example Cour 2");
        result.Season.Should().BeNull();
        result.Episode.Should().Be(3);
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData("S01E01-E03", 1, 3, RuleNumberingKind.InclusiveRange)]
    [InlineData("S01E01~E03", 1, 3, RuleNumberingKind.InclusiveRange)]
    [InlineData("S01E01E02E03", 1, 3, RuleNumberingKind.ExplicitList)]
    [InlineData("S01E01E03E05", null, null, RuleNumberingKind.ExplicitList)]
    [InlineData("S01E03-E01", null, null, RuleNumberingKind.InclusiveRange)]
    public async Task EpisodeListsAndRangesRetainTheirActualSemantics(string token, int? first, int? end, RuleNumberingKind kind)
    {
        RuleParseResult result = await Parse($"Example.{token}.mkv", null);
        result.Episode.Should().Be(first);
        result.EpisodeEnd.Should().Be(end);
        result.NumberingEvidence.Should().Contain(e => e.Kind == kind);
        if (first is null) result.Confidence.Should().BeLessThan(0.5);
        if (token == "S01E01E03E05")
            result.NumberingEvidence!.Single(e => e.Kind == kind).Values.Should().Equal(1, 3, 5);
    }

    [Theory]
    [InlineData("第一二季")]
    [InlineData("第十十季")]
    [InlineData("第二十三十季")]
    [InlineData("第壹佰季")]
    [InlineData("第零二季")]
    public async Task MalformedChineseSeasonCannotUsePartialCapture(string token)
    {
        RuleParseResult result = await Parse($"Example {token} 第03集.mkv", null);
        result.Season.Should().BeNull();
        result.Episode.Should().Be(3);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.InvalidNumber && e.Field == "season");
    }

    [Theory]
    [InlineData("1920x1080.HEVC.Main10p")]
    [InlineData("1080x1920.X265.AV1")]
    [InlineData("1917")]
    [InlineData("1984")]
    [InlineData("1.11")]
    public async Task TechnicalAndTitleNumbersCannotCreateCoordinates(string token)
    {
        RuleParseResult result = await Parse($"Example.{token}.mkv", null);
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData("OVA01")]
    [InlineData("SP02")]
    [InlineData("OAD03")]
    public async Task ContentKindDoesNotInventSpecialsSeasonOrEpisode(string token)
    {
        await PrepareStructuredRules(1);
        RuleParseResult result = await Parse($"Example {token}.mkv", null);
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
        result.Confidence.Should().BeLessThan(0.5);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.ContentKind);
        RuleParseResult literal = await Parse($"Example {token}.S00E03.mkv", null);
        literal.Season.Should().Be(0);
        // 旧种子若优先捕获不同编号，应保留冲突，不能默许特殊种类自带季内号。
        literal.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Season && e.Value == 0);
    }

    [Fact]
    public async Task ForcedMovieNeverReceivesStructuredCoordinates()
    {
        SeedRule(new ParseRule { Name = "强制电影", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^(?<title>Example 3)", ForceType = true, DefaultType = "movie" });
        RuleParseResult result = await Parse("Example 3.S02E03.Vol02.mkv", null);
        result.Title.Should().Be("Example 3");
        result.MediaType.Should().Be("movie");
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData("Movie 43")]
    [InlineData("Trailer Park Boys")]
    public async Task OrdinaryContentWordsDoNotBecomeSpecials(string title)
    {
        RuleParseResult result = await Parse($"{title}.2024.mkv", null);
        result.Episode.Should().BeNull();
        result.Season.Should().BeNull();
        result.RejectedFields.Should().BeNullOrEmpty();
    }

    private static void AssertStructuredSpans(RuleParseResult result, FileParseContext context)
    {
        result.NumberingEvidence.Should().NotBeNullOrEmpty();
        foreach (RuleNumberingEvidence item in result.NumberingEvidence!)
        {
            string source = item.SegmentIndex is int index ? context.RelativeSegments[index] : context.FileName;
            item.Source.Should().Be(item.SegmentIndex is null ? "FileName" : "RelativeSegment");
            item.Start.Should().BeGreaterThanOrEqualTo(0);
            item.Length.Should().BeGreaterThan(0);
            source.Substring(item.Start, item.Length).Should().Be(item.Token);
            item.RuleKey.Should().Be($"builtin:structured:{item.Kind}");
        }
    }

    [Theory]
    [InlineData("Example.11.5v2.mkv")]
    [InlineData("Example_11.5v2.1080p.mkv")]
    [InlineData("Example-11.5v2.mkv")]
    public async Task DelimitedBareFractionCannotBeReinjectedFromParent(string file)
    {
        RuleParseResult result = await Parse(file, "Example S01E11");
        result.Episode.Should().BeNull();
        result.EpisodeEnd.Should().BeNull();
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.FractionalEpisode && e.TextValue == "11.5");
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData("Example.S01E01-E02.S01E01-E03.mkv", 2)]
    [InlineData("Example.S01E01.S01E01-E03.mkv", null)]
    public async Task SameStartDifferentEndsAreExplicitRangeConflicts(string file, int? firstEnd)
    {
        RuleParseResult result = await Parse(file, null);
        result.Episode.Should().Be(1);
        result.EpisodeEnd.Should().Be(firstEnd);
        result.Conflicts.Should().Contain(c => c.StartsWith("episodeEnd："));
        result.NumberingEvidence.Should().Contain(e => e.Field == "episode" && e.State == RuleEvidenceState.Conflict);
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData("Example - 01 (Flames of Destiny).mkv")]
    [InlineData("Example - 01 (1080p Finale).mkv")]
    [InlineData("Example - 01 (CR 1080p AAC MKV) [Side Story].mkv")]
    public async Task SharedEvidenceCannotBypassUnknownDashTailGuard(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Episode.Should().BeNull();
        result.NumberingEvidence.Should().NotContain(e => e.Field == "episode" && e.State == RuleEvidenceState.Accepted);
    }

    [Theory]
    [InlineData("Example.S01E02.1080p.AAC 5.0.mkv")]
    [InlineData("Example.S01E02 [AAC] [5.1].mkv")]
    [InlineData("Example.S01E02 [5.1] [AAC].mkv")]
    [InlineData("Example.S01E02.1080p.DDP.5.1.mkv")]
    public async Task AudioContextProtectsChannelsFromFractionalEpisodeGate(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Season.Should().Be(1);
        result.Episode.Should().Be(2);
        result.RejectedFields.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("Example.S01E05.1.AAC.mkv")]
    [InlineData("Example.S01E05.1 [AAC] [5.1].mkv")]
    [InlineData("Example.2x05.1.AAC.mkv")]
    public async Task AudioContextCannotAuthorizeExplicitFractionalEpisode(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Episode.Should().BeNull();
        result.RejectedFields.Should().Contain("episode");
    }

    [Fact]
    public async Task ExistingVarietyIssueRulePreservesItsExplicitLocalEpisodeMeaning()
    {
        await PrepareStructuredRules(1);
        RuleParseResult result = await Parse("Example 第300期.mkv", null);
        result.Season.Should().BeNull();
        result.Episode.Should().Be(300);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Issue && e.Value == 300);
    }

    [Fact]
    public async Task UnmappedIssueCannotInventSeasonOrCurrentEpisode()
    {
        RuleParseResult result = await Parse("Example 第2期.mkv", null);
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Issue && e.Value == 2);
    }

    [Theory]
    [InlineData("02.mkv", null)]
    [InlineData("02.E03.mkv", 3)]
    public async Task ExplicitUserVolumeNamespaceBlocksOnlyTheOverlappingBareNumber(string file, int? episode)
    {
        SeedRule(new ParseRule { Name = "纯号卷数", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^(?<volume>02)", DefaultType = "tv", ConfidenceBonus = 1 });
        RuleParseResult result = await Parse(file, "Example Season 1");
        result.Season.Should().Be(1);
        result.Episode.Should().Be(episode);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Volume && e.Value == 2
            && e.Source == "FileName" && e.Start == 0 && e.Length == 2 && e.Token == "02"
            && e.RuleKey!.StartsWith("user:"));
        if (episode is null) result.Confidence.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData(ParseScope.RelativePath)]
    [InlineData(ParseScope.FullPath)]
    public async Task UserNamespacePathCaptureMapsToActualFileSpan(ParseScope scope)
    {
        string separator = scope == ParseScope.RelativePath ? "/" : System.Text.RegularExpressions.Regex.Escape(Path.DirectorySeparatorChar.ToString());
        SeedRule(new ParseRule { Name = "路径卷数", Enabled = true, Priority = 1, Scope = scope,
            Pattern = "^Example Season 1" + separator + @"(?<volume>02)\.mkv$", DefaultType = "tv" });
        RuleParseResult result = await Parse("02.mkv", "Example Season 1");
        result.Episode.Should().BeNull();
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Volume && e.RuleKey!.StartsWith("user:")
            && e.Source == "FileName" && e.SegmentIndex == null && e.Start == 0 && e.Token == "02");
    }

    [Fact]
    public async Task UserNamespaceCrossSegmentCaptureCannotInventLocalSourceSpan()
    {
        SeedRule(new ParseRule { Name = "跨段卷数", Enabled = true, Priority = 1, Scope = ParseScope.RelativePath,
            Pattern = @"^Example/(?<volume>1/02)\.mkv$", DefaultType = "tv" });
        FileParseContext context = new("02.mkv", "02.mkv", null, ["Example", "1"]);
        RuleParseResult result = await _sut.ParseAsync(context);
        result.Episode.Should().BeNull();
        RuleNumberingEvidence evidence = result.NumberingEvidence!.Single(e => e.RuleKey!.StartsWith("user:"));
        evidence.Source.Should().Be("RelativePath");
        evidence.State.Should().Be(RuleEvidenceState.Rejected);
        string.Join('/', context.RelativeSegments.Concat([context.FileName])).Substring(evidence.Start, evidence.Length)
            .Should().Be(evidence.Token).And.Be("1/02");
    }

    [Theory]
    [InlineData("21st", 21)]
    [InlineData("11th", 11)]
    [InlineData("2nd", 2)]
    [InlineData("11st", null)]
    public async Task OrdinalSeasonNeverReusesFollowingDashEpisodeAsSeason(string ordinal, int? season)
    {
        RuleParseResult result = await Parse($"Example {ordinal} Season - 01.mkv", null);
        result.Season.Should().Be(season);
        result.Episode.Should().Be(1);
        result.Conflicts.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("WALL-E.2008.1080p.mkv", null)]
    [InlineData("Example.E2008.mkv", 2008)]
    [InlineData("Example.EP2008.mkv", 2008)]
    [InlineData("Example.Episode 2008.mkv", 2008)]
    public async Task SingleLetterBeforeYearNeedsDirectEpisodeNumber(string file, int? episode)
    {
        RuleParseResult result = await Parse(file, null);
        result.Episode.Should().Be(episode);
        if (episode is null) result.Title.Should().Be("WALL E");
    }

    [Theory]
    [InlineData("[00-25TV全集+剧场版]")]
    [InlineData("[00-25TV全集+OVA]")]
    [InlineData("[00-25TV全集+SP+特典]")]
    public async Task MixedParentPackageContentCannotReclassifyCurrentFile(string package)
    {
        RuleParseResult result = await Parse("[Group-Raws][Example Series][07][1080P].mkv",
            $"[Group-Raws][Example Series]{package}[1080P][BDRip]");
        result.Episode.Should().Be(7);
        result.MediaType.Should().Be("tv");
        result.Conflicts.Should().BeNullOrEmpty();
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.ContentKind
            && e.SegmentIndex == 0 && e.State == RuleEvidenceState.Candidate && e.Reason!.Contains("包范围"));
    }

    [Theory]
    [InlineData("Example.E01.mkv", "[劇場版]")]
    [InlineData("Example.剧场版.E01.mkv", "Example [00-25TV全集+剧场版]")]
    public async Task SingleContentScopeStillConflictsWithExplicitEpisode(string file, string parent)
    {
        RuleParseResult result = await Parse(file, parent);
        result.Conflicts.Should().Contain(c => c.StartsWith("mediaType："));
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData("Example C.E.73 Chapter.mkv", null, null)]
    [InlineData("Example C.E.73 Chapter.mkv", "Season 2", null)]
    [InlineData("Example E.03.mkv", null, null)]
    [InlineData("Example E.03.mkv", "Season 2", 3)]
    [InlineData("Example E03.mkv", null, 3)]
    [InlineData("Example EP 03.mkv", null, 3)]
    public async Task SeparatedSingleLetterNeedsSeasonAndCannotEscapeAbbreviation(string file, string? parent, int? episode)
    {
        RuleParseResult result = await Parse(file, parent);
        result.Episode.Should().Be(episode);
        if (file.Contains("C.E.")) result.Title.Should().Contain("C E 73 Chapter");
        if (episode is null) result.NumberingEvidence.Should().Contain(e => e.Field == "episode" && e.State == RuleEvidenceState.Candidate);
    }

    [Fact]
    public async Task AirDateCannotEraseIndependentUserDeclaredIssueEpisode()
    {
        SeedRule(new ParseRule { Name = "节目期号", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^\d{8}-第(?<episode>\d+)期", DefaultType = "tv" });
        RuleParseResult result = await Parse("20260909-第7期上.mp4", "Example S02");
        result.Season.Should().Be(2);
        result.Episode.Should().Be(7);
        result.Year.Should().BeNull();
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.AirDate && e.State == RuleEvidenceState.Candidate);
        result.NumberingEvidence.Should().Contain(e => e.Kind == RuleNumberingKind.Issue && e.Value == 7);
    }
}
