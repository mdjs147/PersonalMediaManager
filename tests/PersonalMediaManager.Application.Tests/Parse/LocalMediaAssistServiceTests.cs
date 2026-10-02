using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;

namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class LocalMediaAssistServiceTests
{
    private readonly ILocalAiSettingsService _settings = Substitute.For<ILocalAiSettingsService>();
    private readonly ILocalAiInferenceClient _client = Substitute.For<ILocalAiInferenceClient>();
    private readonly ITmdbSearchService _tmdb = Substitute.For<ITmdbSearchService>();
    private readonly LocalMediaAssistService _sut;
    private static readonly FileParseContext Source = FileParseContext.FileNameOnly("Example S01E02.mkv");
    private const string Valid = """{"index":1}""";

    public LocalMediaAssistServiceTests()
    {
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto { Mode = LocalAiMode.AfterRules });
        _client.GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocalAiInferenceResult(Valid, null, "stop"));
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>()).Returns(new TmdbSearchResult([], null));
        _sut = new(_settings, _client, _tmdb, new(), new SystemClock());
    }

    [Theory]
    [InlineData(LocalAiMode.Disabled, LocalAiMode.BeforeRules)]
    [InlineData(LocalAiMode.Disabled, LocalAiMode.AfterRules)]
    [InlineData(LocalAiMode.BeforeRules, LocalAiMode.AfterRules)]
    [InlineData(LocalAiMode.AfterRules, LocalAiMode.BeforeRules)]
    public async Task DisabledOrOtherPhaseMakesNoInference(LocalAiMode mode, LocalAiMode requested)
    {
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto { Mode = mode });
        LocalMediaAssistResult result = await _sut.SuggestAsync(Source, null, requested);
        result.Status.Should().Be("NotScheduled");
        await _client.DidNotReceive().GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheReusesOnlySameModelSettingsAndActualInput()
    {
        LocalMediaAssistResult first = await _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules);
        LocalMediaAssistResult second = await _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules);
        first.Candidates.Single().Kind.Should().Be("LiteralTitle");
        second.FromCache.Should().BeTrue();
        await _client.Received(1).GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>());
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto { Mode = LocalAiMode.AfterRules, Threads = 1 });
        await _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules);
        await _client.Received(2).GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheDoesNotReuseAnIncorrectOriginalSegmentOffset()
    {
        _client.GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocalAiInferenceResult("""{"index":0}""", null));
        FileParseContext first = new("01.mkv", "01.mkv", null, ["RootA", "Example Title"]);
        FileParseContext second = new("01.mkv", "01.mkv", null, ["Prefix", "RootA", "Example Title"]);
        (await _sut.SuggestAsync(first, null, LocalAiMode.AfterRules)).Candidates.Single().SegmentIndex.Should().Be(1);
        LocalMediaAssistResult result = await _sut.SuggestAsync(second, null, LocalAiMode.AfterRules);
        result.Candidates.Single().SegmentIndex.Should().Be(2);
        result.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task PromptOmitsAbsolutePathAndRuleConfidence()
    {
        RuleParseResult rule = new("Example", 2024, "tv", 1, 2, null, 0.97543, false, null);
        FileParseContext source = new("/private/root/Example.mkv", "Example.mkv", "/private/root", ["Example"]);
        await _sut.SuggestAsync(source, rule, LocalAiMode.AfterRules);
        await _client.Received().GenerateAsync(Arg.Is<LocalAiInferenceRequest>(r =>
            r.UserPrompt.Contains("Example") && !r.UserPrompt.Contains("/private")
            && !r.UserPrompt.Contains("0.97543") && !r.UserPrompt.Contains("Confidence")
            && !r.UserPrompt.Contains("ruleHint") && r.MaxOutputTokens == 32 && r.AllowedSpanCount > 0), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{\"index\":0,\"index\":null}", "DuplicateProperty")]
    [InlineData("{\"index\":{},\"title\":\"Example\"}", "InvalidIndexShape")]
    [InlineData("{\"index\":true}", "IndexOutsideClosedSet")]
    [InlineData("{\"index\":\"1\"}", "IndexOutsideClosedSet")]
    [InlineData("{\"index\":1.0}", "IndexOutsideClosedSet")]
    [InlineData("{\"index\":-1}", "IndexOutsideClosedSet")]
    [InlineData("{\"index\":999}", "IndexOutsideClosedSet")]
    [InlineData("{\"candidates\":[]}", "InvalidIndexShape")]
    [InlineData("not json", "InvalidJson")]
    public void InvalidStructureIsExplicitlyRejected(string raw, string reason)
    {
        LocalMediaAssistResult result = Parse(raw, Source.FileName);
        result.Status.Should().Be("Rejected");
        result.Reasons.Should().Contain(reason);
        result.Candidates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Up")]
    [InlineData("IT")]
    [InlineData("Pi")]
    public void ShortAmbiguousModelSpansAbstainWithoutChangingRuleTitles(string title)
    {
        LocalMediaAssistResult result = Parse("""{"index":0}""", title + ".mkv");
        result.Candidates.Should().BeEmpty();
        result.OfferedCandidates.Should().ContainSingle().Which.Title.Should().Be(title);
    }

    [Theory]
    [InlineData("1080p")]
    [InlineData("HD官网中英双字")]
    [InlineData("迅雷下载")]
    [InlineData("夸克下载")]
    [InlineData("DV")]
    [InlineData("SC")]
    [InlineData("TC")]
    [InlineData("GB")]
    [InlineData("HQ")]
    [InlineData("BD")]
    [InlineData("HEVC")]
    [InlineData("WEB-DL")]
    [InlineData("ASSx2")]
    [InlineData("2025-01-09 News")]
    [InlineData("www.example.com")]
    public void FrozenProtocolObviousNoiseNeverBecomesQuery(string evidence) =>
        Parse("""{"index":0}""", evidence + ".mkv").Candidates.Should().BeEmpty();

    [Fact]
    public void SelectedQueryAndEvidenceAreRestoredWithoutNormalization()
    {
        const string title = "Evangelion 2.22 You Can (Not) Advance";
        LocalMediaAssistResult result = Parse("""{"index":0}""", title + ".mkv");
        LocalMediaSuggestion selected = result.Candidates.Should().ContainSingle().Which;
        selected.Title.Should().Be(title); selected.Evidence.Should().Be(title);
        result.Reasons.Should().Contain("LiteralSpanOnlyNotIdentityVerification");
    }

    [Theory]
    [InlineData("title")]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("year")]
    [InlineData("season")]
    public void ExtraIdentityOrTitleFieldsRejectEntireSelection(string field) =>
        Parse("{\"index\":1,\"" + field + "\":\"invented\"}", Source.FileName).Status.Should().Be("Rejected");

    [Fact]
    public void NullIsExplicitAbstentionWithAuditableOfferedPool()
    {
        LocalMediaAssistResult result = Parse("""{"index":null}""", Source.FileName);
        result.Status.Should().Be("Validated"); result.Candidates.Should().BeEmpty();
        result.Reasons.Should().Contain("Abstain"); result.OfferedCandidates.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedSourceOrPoolDoesNotMakeInference(bool oversizedSource)
    {
        string name = oversizedSource ? new string('A', 513) + ".mkv"
            : string.Join(" ", Enumerable.Range(0, 14).Select(i => $"[Long Candidate Name {i} Text]")) + ".mkv";
        LocalMediaAssistResult result = await _sut.SuggestAsync(FileParseContext.FileNameOnly(name), null, LocalAiMode.AfterRules);
        result.Status.Should().Be("Rejected");
        await _client.DidNotReceive().GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>());
    }

    private static LocalMediaAssistResult Parse(string raw, string file) =>
        LocalMediaAssistService.Parse(raw, file, null, null, LocalAiMode.BeforeRules, "model");

    [Fact]
    public async Task TruncationAndRuntimeFailureAreNotCached()
    {
        _client.GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocalAiInferenceResult(Valid, null, "length"));
        (await _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules)).Candidates.Should().BeEmpty();
        await _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules);
        await _client.Received(2).GenerateAsync(Arg.Any<LocalAiInferenceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationPropagatesEvenForCachedInput()
    {
        await _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules);
        using CancellationTokenSource ct = new(); ct.Cancel();
        Func<Task> action = () => _sut.SuggestAsync(Source, null, LocalAiMode.AfterRules, ct.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task QueriesHaveOneTotalBudgetTypedDedupAndNoHiddenFallbackVariants()
    {
        LocalMediaAssistResult result = Suggestions("One", "Two", "Three");
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new TmdbSearchResult([Candidate(7, call.Arg<TmdbSearchRequest>().MediaType)], null, true));
        LocalMediaLookupResult lookup = await _sut.LookupAsync(result, [], "zh-CN");
        lookup.Queries.Should().HaveCount(2);
        lookup.Candidates.Should().HaveCount(2, "同数值ID的电影和剧集是两种身份；重复别名不能增票");
        await _tmdb.Received(2).SearchAsync(Arg.Is<TmdbSearchRequest>(r => r.Year == null
            && r.Language == "zh-CN" && r.FallbackLanguage == "zh-CN"), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
    }

    [Fact]
    public async Task ExistingTvQueryDoesNotHideUnsearchedMovieInterpretation()
    {
        LocalMediaLookupResult result = await _sut.LookupAsync(Suggestions("Example"),
            [new("Example", "tv", null, "zh-CN", "zh-CN")], "zh-CN");
        result.Queries.Should().ContainSingle().Which.MediaType.Should().Be("movie");
    }

    [Theory]
    [InlineData("Evangelion 222", "Evangelion 2.22")]
    [InlineData("AB", "A+B")]
    [InlineData("ExampleTitle", "Example Title")]
    public async Task QueryDedupPreservesDecimalPunctuationAndWordBoundaries(string prior, string literal)
    {
        LocalMediaLookupResult result = await _sut.LookupAsync(Suggestions(literal),
            [new(prior, "tv", null, "zh-CN", "zh-CN"), new(prior, "movie", null, "zh-CN", "zh-CN")], "zh-CN");
        result.Queries.Should().HaveCount(2).And.OnlyContain(query => query.Title == literal);
    }

    [Fact]
    public async Task FailureStopsFurtherQueriesWithoutFabricatingEmptySuccess()
    {
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException());
        LocalMediaLookupResult result = await _sut.LookupAsync(Suggestions("One", "Two", "Three"), [], "zh-CN");
        result.Incomplete.Should().BeTrue();
        result.Queries.Should().ContainSingle().Which.Status.Should().Be("Failed");
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationDuringSearchStopsEntireRemainingBudget()
    {
        using CancellationTokenSource ct = new();
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        { ct.Cancel(); await Task.Delay(10, call.Arg<CancellationToken>()); return new TmdbSearchResult([], null); });
        Func<Task> action = () => _sut.LookupAsync(Suggestions("One", "Two"), [], "zh-CN", ct.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        await _tmdb.Received(1).SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>());
    }

    private static LocalMediaAssistResult Suggestions(params string[] titles) => new(LocalAiMode.AfterRules, "Validated",
        titles.Select(t => new LocalMediaSuggestion(t, t, "FileName", null, 0, t.Length, "source", "LiteralTitle")).ToArray(), []);
    private static TmdbCandidate Candidate(int id, string type) => new(id, type, "Example", "Example", null, 1000, "en", null, null, null);
}
