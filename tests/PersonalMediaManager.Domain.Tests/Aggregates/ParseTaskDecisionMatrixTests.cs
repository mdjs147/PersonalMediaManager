using PersonalMediaManager.Domain.Aggregates.ParseTasks;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>ParseTask 决策矩阵：§3.3.2 6 行第一次 + §3.3.2 3 行 AI 后第二次</summary>
public sealed class ParseTaskDecisionMatrixTests
{
    [Fact]
    public void Rule_LowConfidence_NoCandidates_GoesToAi()
    {
        ParseTask t = ParseTask.AfterRuleEngine(confidence: 0.4, hasSpecialChars: false);
        t.DecideAfterFirstTmdb().Should().Be(NextAction.CallAi);
    }

    [Fact]
    public void Rule_SpecialChars_OverrideAllAndGoesToAi()
    {
        ParseTask t = ParseTask.AfterRuleEngine(confidence: 0.99, hasSpecialChars: true);
        t.DecideAfterFirstTmdb().Should().Be(NextAction.CallAi);
    }

    [Fact]
    public void Rule_HighConfidence_NoTmdbYet_GoesToTmdbToQuery()
    {
        ParseTask t = ParseTask.AfterRuleEngine(confidence: 0.8, hasSpecialChars: false);
        t.DecideAfterFirstTmdb().Should().Be(NextAction.UseTmdb);
    }

    [Theory]
    [InlineData(1, NextAction.UseTmdb)]   // 唯一
    [InlineData(3, NextAction.UseTmdb)]   // 等于 N（默认 3）
    [InlineData(4, NextAction.CallAi)]    // 超过 N → AI
    [InlineData(0, NextAction.CallAi)]    // 零结果 → AI
    public void AfterTmdb_BehavesByCandidateCount(int candidates, NextAction expected)
    {
        ParseTask t = ParseTask.AfterTmdbQuery(confidence: 0.8, candidateCount: candidates, hasSpecialChars: false);
        t.DecideAfterFirstTmdb().Should().Be(expected);
    }

    [Theory]
    [InlineData(0, NextAction.SendToReview)]
    [InlineData(1, NextAction.UseTmdb)]
    [InlineData(3, NextAction.UseTmdb)]
    [InlineData(4, NextAction.SendToReview)]
    public void AfterAiRetmdb_GoesToReviewOrUse(int candidates, NextAction expected)
    {
        ParseTask.DecideAfterAiRetmdb(candidates).Should().Be(expected);
    }
}
