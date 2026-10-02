using System.Globalization;
using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Parse;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class RuleEngineServiceTests
{
    [Theory]
    [InlineData("Example.S01E10000.mkv")]
    [InlineData("Example.AS01E02.mkv")]
    [InlineData("Example.S01E02nd.mkv")]
    public async Task ExecutionSafety_LegacyPartialTokenCannotBypassStructuredBoundaries(string file)
    {
        RuleParseResult result = await Parse(file, null);
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData(".*")]
    [InlineData("^(?<episode>)(?<title>)")]
    public async Task ExecutionSafety_EmptyCaptureCannotSwallowLaterEffectiveRule(string weakPattern)
    {
        SeedRule(new ParseRule { Name = "无有效捕获", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = weakPattern, DefaultType = "tv", ConfidenceBonus = 1 });
        SeedRule(new ParseRule { Name = "有效捕获", Enabled = true, Priority = 2, Scope = ParseScope.FileName,
            Pattern = @"^(?<title>Example)\.S(?<season>01)E(?<episode>02)", DefaultType = "tv" });
        RuleParseResult result = await Parse("Example.S01E02.mkv", null);
        result.Title.Should().Be("Example");
        result.Episode.Should().Be(2);
        result.Diagnostics!.RulesEvaluated.Should().Be(2);
        result.Diagnostics.Status.Should().Be("complete");
        result.Diagnostics.Events.Should().Contain(e => e.Outcome == "rejected" && e.Reason == "NoEffectiveCapture");
    }

    [Fact]
    public async Task ExecutionSafety_InvalidStoredPatternCannotProduceTrustedFallback()
    {
        SeedRule(new ParseRule { Name = "损坏的历史规则", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = "[", DefaultType = "tv" });
        RuleParseResult result = await Parse("Example.S01E02.mkv", null);
        result.Title.Should().Be("Example");
        result.Diagnostics!.Status.Should().Be("incomplete");
        result.Diagnostics.Events.Should().Contain(e => e.Reason == "InvalidPattern");
        result.Conflicts.Should().Contain("executionIncomplete");
        result.HasIdentityEvidence.Should().BeFalse();
        result.Confidence.Should().BeLessThan(0.5);
        result.Episode.Should().BeNull();
        result.RejectedFields.Should().Contain("episode");
    }

    [Fact]
    public async Task ExecutionSafety_RegexTimeoutIsAuditedAndDoesNotEscape()
    {
        SeedRule(new ParseRule { Name = "回溯测试", Enabled = true, Priority = 1, Scope = ParseScope.FileName,
            Pattern = @"^(?<title>(a+)+)$", DefaultType = "tv" });
        RuleParseResult result = await Parse(new string('a', 3000) + "!.mkv", null);
        result.Diagnostics!.Status.Should().Be("incomplete");
        result.Diagnostics.Events.Should().Contain(e => e.Reason == "RegexTimeout");
        result.HasIdentityEvidence.Should().BeFalse();
        result.Confidence.Should().BeLessThan(0.5);
    }

    [Fact]
    public async Task ExecutionSafety_TotalBudgetStopsRemainingPathologicalRules()
    {
        for (int i = 0; i < 8; i++)
            SeedRule(new ParseRule { Name = $"回溯预算{i}", Enabled = true, Priority = i,
                Scope = ParseScope.FileName, Pattern = $@"^(a+)+$(?#budget{i})" });
        RuleParseResult result = await Parse(new string('a', 12000) + "!.mkv", null);
        result.Diagnostics!.FaultReasons.Should().Contain("RegexBudgetExceeded");
        result.Diagnostics.RulesEvaluated.Should().BeLessThan(8);
        result.Confidence.Should().BeLessThan(0.5);
        result.Conflicts.Should().Contain("executionIncomplete");
    }

    [Fact]
    public async Task ExecutionSafety_OverlongInputCannotEscapeFinalSeal()
    {
        RuleParseResult result = await Parse(new string('a', 32769) + ".S01E02.mkv", null);
        result.Diagnostics!.Status.Should().Be("incomplete");
        result.Diagnostics.Events.Should().Contain(e => e.Reason == "RegexInputLimit");
        result.Episode.Should().BeNull();
        result.HasIdentityEvidence.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutionSafety_CallerCancellationIsNotReportedAsNoMatch()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Func<Task> parse = () => Parse("Example.S01E02.mkv", null, cancelled.Token);
        await parse.Should().ThrowAsync<OperationCanceledException>();
        ParseRuleService preview = new(_dbFactory);
        Action test = () => preview.TestAsync(new("(?<title>Example)", "Example"), cancelled.Token);
        test.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecutionSafety_PreviewAndProductionUseIdenticalInvariantOptions()
    {
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            const string pattern = @"^(?<title>INDIGO)\.mkv$";
            SeedRule(new ParseRule { Name = "大小写一致", Enabled = true, Priority = 1,
                Scope = ParseScope.FileName, Pattern = pattern });
            TestParseRuleResponse preview = await new ParseRuleService(_dbFactory).TestAsync(new(pattern, "indigo.mkv"));
            RuleParseResult result = await Parse("indigo.mkv", null);
            preview.Matched.Should().BeTrue();
            result.MatchedRuleId.Should().NotBeNull();
            result.Title.Should().Be(preview.Groups["title"]);
            Regex expression = RuleRegexExecution.Get(pattern);
            RuleRegexExecution.Get(pattern).Should().BeSameAs(expression);
            expression.Options.Should().HaveFlag(RegexOptions.CultureInvariant);
            result.Diagnostics!.RegexCacheHits.Should().BeGreaterThan(0);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void ExecutionSafety_PreviewRejectsOversizedSamples()
    {
        Action test = () => new ParseRuleService(_dbFactory).TestAsync(new(".*", new string('x', 32769)));
        test.Should().Throw<BusinessException>();
    }

    [Fact]
    public async Task ExecutionSafety_SeparatorOnlyCaptureCannotHideConcreteRule()
    {
        SeedRule(new ParseRule { Name = "仅分隔符", Enabled = true, Priority = 1,
            Scope = ParseScope.FileName, Pattern = @"^(?<title>_+)" });
        SeedRule(new ParseRule { Name = "有意义标题", Enabled = true, Priority = 2,
            Scope = ParseScope.FileName, Pattern = @"^_+(?<title>Example)\.S01E02" });
        RuleParseResult result = await Parse("__Example.S01E02.mkv", null);
        result.Title.Should().Be("Example");
        result.Diagnostics!.RulesEvaluated.Should().Be(2);
    }

    [Fact]
    public async Task ExecutionSafety_UserTitleParenthesesRemainIntact()
    {
        SeedRule(new ParseRule { Name = "完整标题", Enabled = true, Priority = 1,
            Scope = ParseScope.FileName, Pattern = @"^(?<title>Example \(Season 2 Finale\))" });
        RuleParseResult result = await Parse("Example (Season 2 Finale).mkv", null);
        result.Title.Should().Be("Example (Season 2 Finale)");
    }

    [Fact]
    public async Task ExecutionSafety_EmptyFileMatchDoesNotHideEffectiveAncestor()
    {
        SeedRule(new ParseRule { Name = "逐层非空", Enabled = true, Priority = 1, Scope = ParseScope.AllAncestors,
            Pattern = @"^(?:(?<title>Example)|)", ForceType = true, DefaultType = "movie" });
        RuleParseResult result = await Parse("E03.mkv", "Example");
        result.Title.Should().Be("Example");
        result.MediaType.Should().Be("movie");
        result.ForceType.Should().BeTrue();
        result.Diagnostics!.Events.Should().Contain(e => e.Outcome == "matched" && e.Source == "RelativeSegment");
    }

    [Fact]
    public async Task ExecutionSafety_TraceIsBoundedAndOmitsPatternAndAbsolutePath()
    {
        for (int i = 0; i < 80; i++)
            SeedRule(new ParseRule { Name = $"未匹配{i}", Enabled = true, Priority = i,
                Scope = ParseScope.FileName, Pattern = $"^absent{i}$" });
        RuleParseResult result = await _sut.ParseAsync(new("/private/example/file.mkv", "Example.S01E02.mkv", "/private", []));
        result.Diagnostics!.TraceTruncated.Should().BeTrue();
        result.Diagnostics.Events.Count.Should().Be(128);
        result.Diagnostics.Status.Should().Be("complete");
        string serialized = System.Text.Json.JsonSerializer.Serialize(result.Diagnostics);
        serialized.Should().NotContain("/private");
        serialized.Should().NotContain("^absent");
    }

    [Fact]
    public void ExecutionSafety_DenseMatchesCannotGrowUnboundedEvidence()
    {
        using RuleRegexExecution.Run run = RuleRegexExecution.Begin(CancellationToken.None);
        IReadOnlyList<Match> matches = RuleRegexExecution.Matches(RuleRegexExecution.Get("x"), new string('x', 1000));
        matches.Count.Should().Be(256);
        RuleParseResult result = run.Complete(new("Example", null, "tv", 1, 2, null, 0.9, false, null));
        result.Diagnostics!.Events.Should().Contain(e => e.Reason == "RegexMatchLimit");
        result.Episode.Should().BeNull();
        result.Conflicts.Should().Contain("executionIncomplete");
    }

    [Fact]
    public void ExecutionSafety_FaultReasonSurvivesTraceTruncation()
    {
        using RuleRegexExecution.Run run = RuleRegexExecution.Begin(CancellationToken.None);
        for (int i = 0; i < 140; i++) run.Trace(new($"rule:{i}", "noMatch"));
        run.Incomplete("RegexBudgetExceeded");
        RuleParseResult result = run.Complete(new("Example", null, "tv", 1, 2, null, 0.9, false, null));
        result.Diagnostics!.TraceTruncated.Should().BeTrue();
        result.Diagnostics.FaultReasons.Should().Contain("RegexBudgetExceeded");
        result.Diagnostics.Events.Count.Should().Be(128);
    }
}
