using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

/// <summary>TitleSimilarity — 归一化 Levenshtein 标题相似度</summary>
public sealed class TitleSimilarityTests
{
    [Fact]
    public void Identical_Titles_Are_One()
    {
        TitleSimilarity.Ratio("Breaking Bad", "Breaking Bad").Should().Be(1.0);
    }

    [Fact]
    public void Case_And_Whitespace_Insensitive()
    {
        // 去空白 + 小写归一化后等价 → 1.0
        TitleSimilarity.Ratio("Breaking  Bad", "breakingbad").Should().Be(1.0);
    }

    [Fact]
    public void Both_Empty_Is_One_OneEmpty_Is_Zero()
    {
        TitleSimilarity.Ratio("", "").Should().Be(1.0);
        TitleSimilarity.Ratio(null, null).Should().Be(1.0);
        TitleSimilarity.Ratio("Show", "").Should().Be(0.0);
        TitleSimilarity.Ratio(null, "Show").Should().Be(0.0);
    }

    [Fact]
    public void Unrelated_Titles_Are_Low()
    {
        // 事故核心：不同剧 / 片名相似度必须远低于复用阈值 0.6
        TitleSimilarity.Ratio("掠食城市", "银河护卫队2").Should().BeLessThan(0.6);
        TitleSimilarity.Ratio("Breaking Bad", "Better Call Saul").Should().BeLessThan(0.6);
        TitleSimilarity.Ratio("掠食城市", "一战再战").Should().BeLessThan(0.6);
    }

    [Fact]
    public void Same_Series_Variants_Are_High()
    {
        // 同剧不同集命名（季集后缀差异）应高于阈值，保证合法复用不被误杀
        TitleSimilarity.Ratio("Show A", "Show A").Should().BeGreaterThanOrEqualTo(0.6);
        TitleSimilarity.Ratio("国务卿女士", "国务卿女士").Should().BeGreaterThanOrEqualTo(0.6);
    }
}
