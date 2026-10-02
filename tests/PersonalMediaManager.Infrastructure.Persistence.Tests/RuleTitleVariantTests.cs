using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Fact]
    public async Task TitleVariants_DynamicRuleStillNormalizesGrandBluePrimary()
    {
        long ruleId = SeedRule(new ParseRule { Name = "动漫字幕组单集", Enabled = true, Priority = 50,
            Scope = ParseScope.FileName,
            Pattern = @"^(?:\[[^\]]{1,40}\]\s*)+(?<title>[^\[\]]+?)\s*-\s*(?<episode>\d{1,4})(?:v\d)?\s*(?:\[|\.|$)",
            DefaultType = "tv", ForceType = false, ConfidenceBonus = 0.05 });
        RuleParseResult result = await Parse("[Example-Subs] GRAND BLUE 碧藍之海 3 - 01 [720P][WEB-DL][AAC AVC][CHT].mp4", "迅雷下载");
        result.MatchedRuleId.Should().Be(ruleId);
        result.Title.Should().Be("碧藍之海");
        result.AlternativeTitles.Should().Contain("GRAND BLUE");
        result.NamingEvidence!.OriginalTitle.Should().Contain("GRAND BLUE 碧藍之海");
        result.NamingEvidence.TitleVariants.Should().Contain(v => v.Title == "碧藍之海" && v.Token == "碧藍之海");
        result.Episode.Should().Be(1);
    }

    [Fact]
    public async Task TitleVariants_GrandBluePreservesTraditionalSourceAndSeparateLatinAlias()
    {
        const string file = "[Example-Subs] GRAND BLUE 碧藍之海 3 - 01 [720P][WEB-DL][AAC AVC][CHT].mp4";
        RuleParseResult result = await Parse(file, "迅雷下载");
        result.Title.Should().Be("碧藍之海");
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == "GRAND BLUE" && v.ScriptHint == "Latin");
        result.NamingEvidence.TitleVariants.Should().Contain(v => v.Title == "碧藍之海" && v.ScriptHint == "Cjk");
        result.NamingEvidence.TitleVariants.Should().NotContain(v => v.Title == "碧蓝之海");
        foreach (RuleTitleVariant variant in result.NamingEvidence.TitleVariants!)
        {
            variant.Source.Should().Be("FileName");
            variant.SegmentIndex.Should().BeNull();
            file.Substring(variant.Start, variant.Length).Should().Be(variant.Token);
            variant.AliasOf.Should().Be(variant.Title == result.Title ? null : result.Title);
            variant.Language.Should().BeNull("文字脚本和字幕标签不能独立证明标题语言");
        }
    }

    [Fact]
    public async Task TitleVariants_GluedMixedNameRemainsWholeWithSourceSpan()
    {
        const string file = "测试机甲SEEDFREEDOM.Example.Mech.Seed.Freedom.2030.720p.WEB-DL.AAC.H264-ExampleGroup.mkv";
        RuleParseResult result = await Parse(file, "夸克下载");
        result.Title.Should().Be("测试机甲SEEDFREEDOM");
        IReadOnlyList<RuleTitleVariant> variants = result.NamingEvidence!.TitleVariants!;
        variants.Should().Contain(v => v.Title == "测试机甲SEEDFREEDOM" && v.ScriptHint == "Mixed");
        variants.Should().Contain(v => v.Title == "Example Mech Seed Freedom"
            && v.Token == "Example.Mech.Seed.Freedom");
        variants.Should().NotContain(v => v.Title == "测试机甲" || v.Title == "SEEDFREEDOM");
        foreach (RuleTitleVariant variant in variants)
            file.Substring(variant.Start, variant.Length).Should().Be(variant.Token);
    }

    [Theory]
    [InlineData("国王排名 Ousama Ranking", "Ousama Ranking")]
    [InlineData("攻殻機動隊 Ghost in the Shell", "Ghost in the Shell")]
    public async Task TitleVariants_LatinScriptDoesNotInventEnglishLanguage(string title, string latin)
    {
        RuleParseResult result = await Parse(title + ".S01E01.mkv", null);
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == latin && v.ScriptHint == "Latin" && v.Language == null);
    }

    [Fact]
    public async Task TitleVariants_ParentSourceUsesActualSegmentIndex()
    {
        const string folder = "测试作品 Test Programme";
        RuleParseResult result = await _sut.ParseAsync(new("E21.mkv", "E21.mkv", null, [folder, "Season 1"]));
        foreach (RuleTitleVariant variant in result.NamingEvidence!.TitleVariants!)
        {
            variant.Source.Should().Be("RelativeSegment");
            variant.SegmentIndex.Should().Be(0);
            folder.Substring(variant.Start, variant.Length).Should().Be(variant.Token);
        }
    }

    [Fact]
    public async Task TitleVariants_UnclassifiedScriptDoesNotBecomeLatin()
    {
        RuleParseResult result = await Parse("Москва.mkv", null);
        result.NamingEvidence!.TitleVariants.Should().Contain(v => v.Title == "Москва"
            && v.ScriptHint == "Other" && v.Language == null);
    }
}
