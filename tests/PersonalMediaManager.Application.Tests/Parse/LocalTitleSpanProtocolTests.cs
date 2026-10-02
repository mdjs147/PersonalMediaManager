using System.Reflection;
using System.Text.Json;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class LocalTitleSpanProtocolTests
{
    // 公开 fixture 为语法等价合成输入，仅验证协议契约，不代表原始请求或模型实测。
    public static IEnumerable<object[]> SyntheticInputs()
    {
        using JsonDocument fixture = Fixture();
        return fixture.RootElement.GetProperty("userPrompts").EnumerateArray()
            .Select((row, index) => new object[] { index, row.GetString()! }).ToArray();
    }

    [Fact]
    public void SystemPromptMatchesSyntheticFixtureProtocol()
    {
        using JsonDocument fixture = Fixture();
        LocalTitleSpanProtocol.SystemPrompt.Should().Be(fixture.RootElement.GetProperty("systemPrompt").GetString());
    }

    [Theory]
    [MemberData(nameof(SyntheticInputs))]
    public void CandidateOrderCodePointOffsetsAndPromptMatchAllFourteenSyntheticInputs(int index, string expected)
    {
        index.Should().BeInRange(0, 13);
        using JsonDocument source = JsonDocument.Parse(expected);
        string file = source.RootElement.GetProperty("fileName").GetString()!;
        string? parent = source.RootElement.GetProperty("parentFolderName").GetString();
        LocalSourceSpanPool pool = LocalTitleSpanProtocol.Build(file, parent);
        pool.Truncated.Should().BeFalse();
        LocalTitleSpanProtocol.UserPrompt(file, parent, pool.Rows).Should().Be(expected);
        foreach (LocalSourceSpan row in pool.Rows)
            (row.Source == "fileName" ? file : parent!).Substring(row.Start, row.End - row.Start).Should().Be(row.Text);
    }

    [Fact]
    public void AstralCharactersUseCodePointsOnWireAndUtf16InAudit()
    {
        const string file = "[🎬] Story😀 2.22 Subtitle.mkv";
        LocalSourceSpanPool pool = LocalTitleSpanProtocol.Build(file, null);
        LocalSourceSpan title = pool.Rows.Single();
        title.Start.Should().Be(5);
        using JsonDocument prompt = JsonDocument.Parse(LocalTitleSpanProtocol.UserPrompt(file, null, pool.Rows));
        prompt.RootElement.GetProperty("spans")[0].GetProperty("start").GetInt32().Should().Be(4);
        LocalMediaAssistResult result = LocalMediaAssistService.Parse("{\"index\":0}", file, null, null,
            LocalAiMode.AfterRules, LocalAiModelIds.Huihui);
        LocalMediaSuggestion selected = result.Candidates.Single();
        file.Substring(selected.Start, selected.Length).Should().Be("Story😀 2.22 Subtitle");
    }

    [Theory]
    [InlineData("Title 1080p𐐀.mkv")]
    [InlineData("Title 𝟙𝟚.mkv")]
    public void UnsupportedSupplementaryLettersOrNumbersAbstainWithoutTruncatingTitle(string file)
    {
        LocalSourceSpanPool pool = LocalTitleSpanProtocol.Build(file, null);
        pool.UnsupportedUnicode.Should().BeTrue(); pool.Rows.Should().BeEmpty();
        LocalMediaAssistService.Parse("{\"index\":0}", file, null, null,
            LocalAiMode.AfterRules, LocalAiModelIds.Qwen).Reasons.Should().Contain("UnsupportedUnicodeCategory");
    }

    [Fact]
    public void ParentDuplicatesKeepFirstFilenameSpan()
    {
        LocalSourceSpanPool pool = LocalTitleSpanProtocol.Build("Example Title.mkv", "Example Title");
        pool.Rows.Should().ContainSingle().Which.Source.Should().Be("fileName");
    }

    [Fact]
    public void TechnicalLookingUnknownTextIsStillOnlyAHypothesis()
    {
        LocalMediaAssistResult result = LocalMediaAssistService.Parse("{\"index\":0}", "SUBTITLE.mkv", null, null,
            LocalAiMode.AfterRules, LocalAiModelIds.Qwen);
        result.Reasons.Should().Contain("LiteralSpanOnlyNotIdentityVerification",
            "冻结噪声规则并不能识别所有伪标题，不能把结构通过宣传为身份核验");
        result.Candidates.Single().Title.Should().Be("SUBTITLE");
    }

    private static JsonDocument Fixture()
    {
        Assembly assembly = typeof(LocalTitleSpanProtocolTests).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("LocalSpanProtocolInputs.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream);
    }
}
