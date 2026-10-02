using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;

namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public class AiTaskSchemaValidationTests
{
    private static AiParseRequest Request(int version = 2) => new("Example 示例 S03E01.mkv",
        Context: new(SchemaVersion: version));

    [Theory]
    [InlineData("\"confidence\":100", "$.confidence", "InvalidConfidence")]
    [InlineData("\"confidence\":\"0.9\"", "$.confidence", "InvalidFieldShape")]
    [InlineData("\"abstain\":\"false\"", "$.abstain", "InvalidFieldShape")]
    [InlineData("\"abstain\":null", "$.abstain", "InvalidFieldShape")]
    [InlineData("\"title\":{\"value\":\"Example\"}", "$.title", "InvalidFieldShape")]
    [InlineData("\"type\":[\"tv\"]", "$.type", "InvalidFieldShape")]
    public void InvalidControlOrIdentityShapeBlocksBeforeNormalization(string property, string path, string code)
    {
        string json = "{" + property + "}";
        AiParseResult result = AiPromptHelpers.ParseTaskContent(json, Request());
        result.Abstained.Should().BeTrue();
        result.Confidence.Should().Be(0);
        result.IsAcceptable(0).Should().BeFalse();
        result.Validation!.ReasonCodes.Should().Contain("InvalidSchema");
        result.Validation.ReasonCodes.Should().NotContain("UnknownEvidence");
        result.Validation.SchemaIssues.Should().Contain(issue => issue.Path == path && issue.Code == code && issue.BlocksAcceptance);
    }

    [Fact]
    public void OptionalMalformedNumbersAreDiscardedAndRuleRecoveryKeepsAttribution()
    {
        AiParseRequest request = Request() with { RuleHintSeason = 3 };
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"tv","season":{"value":4},"episode":1,"year":"餐之皿","confidence":0.9}
            """, request);
        result.Abstained.Should().BeFalse();
        result.Season.Should().Be(3);
        result.Year.Should().BeNull();
        result.Validation!.RejectedFields.Should().Contain("season").And.Contain("year");
        result.Validation.ReasonCodes.Should().Contain("KnownFieldChanged");
        result.Validation.SchemaIssues.Should().Contain(issue => issue.Path == "$.season" && !issue.BlocksAcceptance);
    }

    [Fact]
    public void NumericStringsAreExplicitlyCoercedAndStillGrounded()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"tv","season":"3","episode":"1","confidence":0.9}
            """, Request());
        result.Season.Should().Be(3);
        result.Episode.Should().Be(1);
        result.Validation!.SchemaIssues.Should().HaveCount(2).And.OnlyContain(issue => issue.Code == "CoercedNumericString");
    }

    [Fact]
    public void OptionalWrongLayersDoNotRejectOtherwiseValidCore()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"tv","season":3,"episode":1,"confidence":0.9,
             "selectedCandidateId":"21","aliases":{"title":"示例"},"details":[{"seriesTitle":"Example"}],"future":{"foo":1}}
            """, Request());
        result.IsAcceptable(0.7).Should().BeTrue();
        result.SelectedCandidateId.Should().BeNull();
        result.Details.Should().BeNull();
        result.Validation!.SchemaIssues.Should().Contain(issue => issue.Path == "$.aliases")
            .And.Contain(issue => issue.Path == "$.details").And.Contain(issue => issue.Path == "$.future")
            .And.Contain(issue => issue.Path == "$.selectedCandidateId");
    }

    [Fact]
    public void ValidMultilingualNestedDetailsRemainUsable()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example 示例","type":"tv","season":3,"episode":1,"confidence":0.9,
             "details":{"seriesTitle":"Example","contentKind":"episode",
               "titleVariants":[{"title":"Example","language":"en","source":"FileName"},{"title":"示例","language":"zh"}],
               "fieldEvidence":[{"field":"season","value":"3","source":"FileName","token":"S03"}],
               "editionTags":[],"conflicts":[],"uncertainFields":["year"]}}
            """, Request());
        result.IsAcceptable(0.7).Should().BeTrue();
        result.Details!.TitleVariants.Should().HaveCount(2);
        result.Details.FieldEvidence.Should().ContainSingle();
        result.Validation!.SchemaIssues.Should().BeEmpty();
    }

    [Fact]
    public void NestedMalformedElementsAndUnsupportedSemanticsHaveSeparateIssues()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"title":"Example","type":"tv","confidence":0.9,"details":{
             "titleVariants":[4,{"title":"Invented"},{"title":"Example","language":{}}],
             "fieldEvidence":[{"field":"season","value":"9","source":"FileName","token":"S09"}],
             "editionTags":["IMAX",4],"contentKind":"invented"}}
            """, Request());
        result.Abstained.Should().BeFalse();
        result.Validation!.SchemaIssues.Should().Contain(issue => issue.Path == "$.details.titleVariants[0]" && issue.Code == "InvalidFieldShape")
            .And.Contain(issue => issue.Path == "$.details.titleVariants[1].title" && issue.Code == "UnsupportedLiteral")
            .And.Contain(issue => issue.Path == "$.details.fieldEvidence[0]" && issue.Code == "UnsupportedEvidence");
        result.Details!.TitleVariants.Should().ContainSingle().Which.Title.Should().Be("Example");
    }

    [Fact]
    public void ExplicitAbstentionStillCarriesOptionalShapeDiagnostics()
    {
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""
            {"abstain":true,"episode":{"value":1},"details":{"editionTags":{}}}
            """, Request());
        result.Validation!.ReasonCodes.Should().Contain("UnknownEvidence");
        result.Validation.SchemaIssues.Should().Contain(issue => issue.Path == "$.episode")
            .And.Contain(issue => issue.Path == "$.details.editionTags");
    }

    [Fact]
    public void LockedIdentityCannotHideRawInvalidCoreShape()
    {
        AiParseRequest request = Request() with { Context = new(SchemaVersion: 2,
            TaskType: AiParseTaskType.FillMissingFields, LockedBinding: new(42, "tv", "Example")) };
        AiParseResult result = AiPromptHelpers.ParseTaskContent("""{"title":{},"confidence":1}""", request);
        result.Title.Should().Be("Example");
        result.SelectedCandidateId.Should().Be(42);
        result.Abstained.Should().BeTrue();
        result.Validation!.SchemaIssues.Should().Contain(issue => issue.Path == "$.title" && issue.BlocksAcceptance);
    }

    [Fact]
    public void V1AndLegacyKeepExistingLenientParsing()
    {
        const string json = """{"title":"Example","type":"tv","season":{},"episode":"1","confidence":100}""";
        AiParseResult legacy = AiPromptHelpers.ParseContent(json);
        AiParseResult v1 = AiPromptHelpers.ParseTaskContent(json, Request(1));
        legacy.Confidence.Should().Be(1);
        v1.Confidence.Should().Be(1);
        v1.Validation!.SchemaIssues.Should().BeNull();
    }
}
