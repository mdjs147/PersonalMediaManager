using System.Text.Json;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class AiMediaSchemaV2Tests
{
    [Theory]
    [InlineData("{\"title\":\"Example\",\"title\":\"Other\",\"type\":\"movie\"}")]
    [InlineData("{\"title\":\"Example\",\"type\":\"movie\",\"details\":{\"seasonTitle\":\"A\",\"seasonTitle\":\"B\"}}")]
    public void DuplicateJsonKeysAreLogicalFailuresNotUnhandledExceptions(string json)
    {
        Action legacy = () => AiPromptHelpers.ParseContent(json);
        Action task = () => AiPromptHelpers.ParseTaskContent(json, new("Example.mkv", Context: new(SchemaVersion: 2)));
        legacy.Should().Throw<AiProviderLogicalException>().WithMessage("*重复属性*");
        task.Should().Throw<AiProviderLogicalException>().WithMessage("*重复属性*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AbstentionDoesNotInventTvType(int version)
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"abstain":true,"type":null}""",
            new("unknown.mkv", Context: new(SchemaVersion: version)));
        result.MediaType.Should().Be("unknown");
        result.IsAcceptable(0).Should().BeFalse();
    }

    [Fact]
    public void UnknownV2TypeSurvivesButCannotPassAcceptance()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"title":"Some Work","type":"unknown","confidence":1}""",
            new("Some Work.mkv", Context: new(SchemaVersion: 2)));
        result.MediaType.Should().Be("unknown");
        result.IsAcceptable(0).Should().BeFalse();
    }

    [Fact]
    public void AbstentionRetainsGroundedEditionAndUncertaintyDetails()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"abstain":true,"details":{"editionTags":["Special Edition"],"uncertainFields":["type"]}}
            """, new("Example Special Edition.mkv", Context: new(SchemaVersion: 2)));
        result.MediaType.Should().Be("unknown");
        result.Details!.EditionTags.Should().Equal("Special Edition");
        result.Details.UncertainFields.Should().Equal("type");
    }

    [Fact]
    public void V1StillRejectsUnknownNonAbstainingType()
    {
        Action parse = () => AiPromptHelpers.ParseTaskContent("""{"title":"Work","type":"unknown","confidence":1}""",
            new("Work.mkv", Context: new()));
        parse.Should().Throw<AiProviderLogicalException>();
    }

    [Fact]
    public void V1WireDoesNotSilentlyGainV2Extensions()
    {
        AiPromptHelpers.PreparedTaskPrompt prepared = AiPromptHelpers.PrepareTaskPrompt(new("Example S02E01.mkv",
            Context: new(SeasonTitle: "Subtitle", EditionTags: ["Remaster"],
                TextEvidence: [new("title", "Example", "FileName", "Example")],
                RuleProvenance: [new("season", 2, "FileName", Token: "S02")])));
        using JsonDocument json = JsonDocument.Parse(prepared.UserPrompt);
        JsonElement task = json.RootElement.GetProperty("task");
        task.TryGetProperty("seasonTitle", out _).Should().BeFalse();
        task.TryGetProperty("textEvidence", out _).Should().BeFalse();
        task.GetProperty("ruleProvenance")[0].TryGetProperty("token", out _).Should().BeFalse();
    }

    [Fact]
    public void RetainedSegmentEvidenceUsesTransmittedIndexes()
    {
        string[] segments = Enumerable.Range(0, 9).Select(i => $"Folder{i}").ToArray();
        AiPromptHelpers.PreparedTaskPrompt prepared = AiPromptHelpers.PrepareTaskPrompt(new("Example.mkv", RelativeSegments: segments,
            Context: new(SchemaVersion: 2, TextEvidence: [new("title", "Folder8", "RelativeSegment", "Folder8", 8)])));
        prepared.Request.RelativeSegments.Should().HaveCount(8);
        prepared.Request.Context!.TextEvidence.Should().ContainSingle().Which.SegmentIndex.Should().Be(7);
    }

    [Fact]
    public void LargeOptionalEvidenceIsShedBeforeRejectingValidCore()
    {
        string token = new('语', 128);
        AiPromptHelpers.PreparedTaskPrompt prepared = AiPromptHelpers.PrepareTaskPrompt(new("Example S03E01.mkv", Context: new(SchemaVersion: 2,
            RuleProvenance: Enumerable.Range(0, 16).Select(_ => new AiFieldEvidence("season", 3, "FileName", Token: token)).ToArray(),
            SeasonTitle: token, EditionTags: Enumerable.Repeat(token, 8).ToArray())));
        prepared.Metadata.Truncated.Should().BeTrue();
        prepared.Metadata.Utf8Bytes.Should().BeLessThanOrEqualTo(AiPromptHelpers.PromptByteBudget);
        prepared.Request.FileName.Should().Be("Example S03E01.mkv");
    }

    [Theory]
    [InlineData(AiOutputDetail.Compact)]
    [InlineData(AiOutputDetail.Expanded)]
    public void BothDetailModesKeepSameEvidenceAndSchema(AiOutputDetail detail)
    {
        AiParseRequest request = new("[Group] Example 餐之皿 S03E02 HD Remaster.mkv", RelativeSegments: ["Example"],
            Context: new(SchemaVersion: 2, SeasonTitle: "餐之皿", EditionTags: ["HD Remaster"],
                RuleConflicts: ["season：输入冲突"], OutputDetail: detail,
                TextEvidence: [new("seasonTitle", "餐之皿", "FileName", "餐之皿")]));
        AiPromptHelpers.PreparedTaskPrompt prepared = AiPromptHelpers.PrepareTaskPrompt(request);
        using JsonDocument json = JsonDocument.Parse(prepared.UserPrompt);
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("task").GetProperty("seasonTitle").GetString().Should().Be("餐之皿");
        json.RootElement.GetProperty("task").GetProperty("outputDetail").GetString().Should().Be(detail.ToString());
        prepared.Metadata.Utf8Bytes.Should().BeLessThanOrEqualTo(AiPromptHelpers.PromptByteBudget);
        AiPromptHelpers.GetTaskSystemPrompt(request).Should().Contain("不能复制候选score").And.Contain("type=unknown");
    }

    [Fact]
    public void DetailedOutputOnlyRetainsLiteralTitleEditionAndEvidence()
    {
        const string output = """
            {"title":"Example","type":"tv","season":3,"episode":2,"confidence":0.8,
             "details":{"seriesTitle":"Example","seasonTitle":"餐之皿","editionTags":["HD Remaster","IMAX"],
               "titleVariants":[{"title":"Example","language":"en"},{"title":"编造译名","language":"zh"}],
               "fieldEvidence":[{"field":"seasonTitle","value":"餐之皿","source":"FileName","token":"餐之皿"},
                 {"field":"season","value":"5","source":"FileName","token":"Season 5"}],
               "uncertainFields":["year"],"contentKind":"episode"}}
            """;
        AiParseResult result = AiPromptHelpers.ParseTaskContent(output,
            new("Example 餐之皿 S03E02 HD Remaster.mkv", Context: new(SchemaVersion: 2)));
        result.Details!.EditionTags.Should().Equal("HD Remaster");
        result.Details.TitleVariants.Should().ContainSingle().Which.Title.Should().Be("Example");
        result.Details.FieldEvidence.Should().ContainSingle().Which.Field.Should().Be("seasonTitle");
        result.Details.SeasonTitle.Should().Be("餐之皿");
    }

    [Fact]
    public void CandidateScoresCannotResolveSameNameWithoutYearEvidence()
    {
        AiParseRequest request = new("Example.mkv", Context: new(SchemaVersion: 2,
            TaskType: AiParseTaskType.DisambiguateCandidates,
            Candidates: [new(1, "movie", "Example", Year: 1990, Score: 0.99), new(2, "movie", "Example", Year: 2024, Score: 0.5)]));
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"selectedCandidateId":1,"type":"movie","confidence":0.99}""", request);
        result.Abstained.Should().BeTrue();
        result.SelectedCandidateId.Should().BeNull();
        result.Confidence.Should().Be(0);
        result.Validation!.ReasonCodes.Should().Contain("AmbiguousCandidateIdentity");
    }

    [Fact]
    public void NumericEvidenceCannotUseSubstringOfResolutionOrDate()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"unknown","details":{"fieldEvidence":[
              {"field":"season","value":"8","source":"FileName","token":"1080P"},
              {"field":"year","value":"2026","source":"FileName","token":"20261002"},
              {"field":"year","value":"2026","source":"FileName","token":"2026"}]}}
            """, new("Example.1080P.20261002.mkv", Context: new(SchemaVersion: 2)));
        result.Details!.FieldEvidence.Should().BeEmpty();
    }

    [Fact]
    public void V2RejectsInventedSearchAliases()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"movie","aliases":["Unrelated Work"],"confidence":1}
            """, new("Example.mkv", Context: new(SchemaVersion: 2)));
        result.SearchAliases.Should().BeEmpty();
        result.Validation!.RejectedFields.Should().Contain("aliases");
    }

    [Fact]
    public void SplitDirectoryDateCannotSupplyWorkYear()
    {
        AiParseResult result = AiParseResultGuard.Validate(new("Example", 2026, "movie", null, null, null, 1),
            new("Example.mkv", RelativeSegments: ["2026", "10", "02"], Context: new(SchemaVersion: 2)));
        result.Year.Should().BeNull();
    }

    [Fact]
    public void SameNameCanBeResolvedByIndependentLiteralWorkYear()
    {
        AiParseRequest request = new("Example.2024.mkv", Context: new(SchemaVersion: 2,
            TaskType: AiParseTaskType.DisambiguateCandidates,
            Candidates: [new(1, "movie", "Example", Year: 1990, Score: 0.99), new(2, "movie", "Example", Year: 2024, Score: 0.5)]));
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"selectedCandidateId":2,"type":"movie","year":2024,"confidence":0.8}""", request);
        result.SelectedCandidateId.Should().Be(2);
        result.Abstained.Should().BeFalse();
    }

    [Theory]
    [InlineData("Example.1920x1080.mkv", 1920)]
    [InlineData("Example.2048p.mkv", 2048)]
    [InlineData("Example.2026.10.02.mkv", 2026)]
    [InlineData("Example.20261002.mkv", 2026)]
    [InlineData("Example.2026-9-2.mkv", 2026)]
    public void LegacyAndVersionedYearGuardsRejectDatesAndTechnicalNumbers(string file, int year)
    {
        AiParseResult raw = new("Example", year, "movie", null, null, null, 1);
        AiPromptHelpers.GroundYear(raw, new(file)).Year.Should().BeNull();
        AiParseResultGuard.Validate(raw, new(file, Context: new(SchemaVersion: 2))).Year.Should().BeNull();
    }
}
