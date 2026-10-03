using NSubstitute;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Tmdb;
namespace PersonalMediaManager.Application.Tests.Parse;
public sealed class LocalMediaAssistBatchTests
{
    [Theory]
    [InlineData(1, 4096, 2)]
    [InlineData(2, 4096, 1)]
    [InlineData(2, 512, 2)]
    public async Task ExplicitLocalBatchNeverExpandsContext(int size, int context, int calls)
    {
        ILocalAiSettingsService settings = Substitute.For<ILocalAiSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto
            { Mode = LocalAiMode.AfterRules, ContextTokens = context, MaxOutputTokens = 256 });
        Client client = new();
        AiBatchOptions options = new() { LocalMaxItems = size };
        LocalMediaAssistService service = new(settings, client, Substitute.For<ITmdbSearchService>(), new(), new SystemClock(), options);
        LocalMediaAssistResult?[] result = new LocalMediaAssistResult?[2];
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        { result[i] = await service.SuggestAsync(FileParseContext.FileNameOnly(i == 0 ? "Alpha.mkv" : "Beta.mkv"), null, LocalAiMode.AfterRules, ct); }).ToArray(), options);
        client.Calls.Should().Be(calls);
        result[0]!.Candidates.Single().Title.Should().Be("Alpha");
        result[1]!.Candidates.Single().Title.Should().Be("Beta");
        if (calls == 1) client.Last!.BatchSpanCounts.Should().HaveCount(2);
    }
    [Fact]
    public void DefaultsPreserveModelOffAndBothBatchSizesOne()
    {
        new LocalAiSettingsDto().Mode.Should().Be(LocalAiMode.Disabled);
        new AiBatchOptions().LocalMaxItems.Should().Be(1);
        new AiBatchOptions().ExternalMaxItems.Should().Be(1);
    }
    [Fact]
    public async Task ValidLocalSiblingSurvivesEarlierFallbackDeadline()
    {
        ILocalAiSettingsService settings = Substitute.For<ILocalAiSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto
            { Mode = LocalAiMode.AfterRules, ContextTokens = 4096, MaxOutputTokens = 256, TimeoutSeconds = 1 });
        SlowFallbackClient client = new();
        LocalMediaAssistService service = new(settings, client, Substitute.For<ITmdbSearchService>(), new(), new SystemClock(), new() { LocalMaxItems = 2 });
        LocalMediaAssistResult? second = null;
        await AiBatchPipeline.RunAsync([
            async ct => { await service.SuggestAsync(FileParseContext.FileNameOnly("Alpha.mkv"), null, LocalAiMode.AfterRules, ct); },
            async ct => { second = await service.SuggestAsync(FileParseContext.FileNameOnly("Beta.mkv"), null, LocalAiMode.AfterRules, ct); }
        ], new());
        second!.Candidates.Single().Title.Should().Be("Beta");
        client.Calls.Should().Be(2);
    }
    private sealed class SlowFallbackClient : ILocalAiInferenceClient
    {
        public int Calls;
        public async Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (request.BatchSpanCounts is null) { await Task.Delay(1500, ct); return new(null, "late"); }
            string id = request.BatchSpanCounts.Keys.Last();
            return new(JsonSerializer.Serialize(new { items = new[] { new { id, result = new { index = 0 } } } }), null,
                "stop", ModelId: LocalAiModelIds.Qwen, Attempted: true);
        }
    }
    [Fact]
    public async Task ExternalCapacity128DoesNotStartAllLocalDeadlinesBeforeSixtyFourCalls()
    {
        ILocalAiSettingsService settings = Substitute.For<ILocalAiSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto
            { Mode = LocalAiMode.AfterRules, ContextTokens = 4096, MaxOutputTokens = 256, TimeoutSeconds = 1 });
        DelayedClient client = new();
        LocalMediaAssistService service = new(settings, client, Substitute.For<ITmdbSearchService>(), new(), new SystemClock(),
            new() { MaxItems = 128, ExternalMaxItems = 128, LocalMaxItems = 2 });
        int validated = 0;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 128).Select<int, Func<CancellationToken, Task>>(index => async ct =>
        {
            LocalMediaAssistResult result = await service.SuggestAsync(FileParseContext.FileNameOnly($"SyntheticWork{index:D3}.mkv"), null, LocalAiMode.AfterRules, ct);
            result.Candidates.Should().HaveCount(1); validated++;
        }).ToArray(), new() { MaxItems = 128, ExternalMaxItems = 128, LocalMaxItems = 2, MaxWaitMilliseconds = 1000 });
        validated.Should().Be(128); client.Calls.Should().Be(64); client.Largest.Should().Be(2);
    }
    private sealed class DelayedClient : ILocalAiInferenceClient
    {
        public int Calls, Largest;
        public async Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken ct = default)
        {
            Calls++; Largest = Math.Max(Largest, request.BatchSpanCounts?.Count ?? 1);
            await Task.Delay(25, ct);
            return new(JsonSerializer.Serialize(new { items = request.BatchSpanCounts!.Keys.Select(id => new { id, result = new { index = 0 } }) }),
                null, "stop", ModelId: LocalAiModelIds.Qwen, Attempted: true);
        }
    }
    [Fact]
    public async Task LocalInvalidBatchesShareOnlyFourWorkerFallbacks()
    {
        ILocalAiSettingsService settings = Substitute.For<ILocalAiSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new LocalAiSettingsDto
            { Mode = LocalAiMode.AfterRules, ContextTokens = 4096, MaxOutputTokens = 256 });
        MissingClient client = new();
        LocalMediaAssistService service = new(settings, client, Substitute.For<ITmdbSearchService>(), new(), new SystemClock(), new() { LocalMaxItems = 2 });
        List<string> status = [];
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 6).Select<int, Func<CancellationToken, Task>>(index => async ct =>
        { status.Add((await service.SuggestAsync(FileParseContext.FileNameOnly($"LocalWork{index}.mkv"), null, LocalAiMode.AfterRules, ct)).Status); }).ToArray(),
            new() { MaxItems = 128, ExternalMaxItems = 128 });
        client.Singles.Should().Be(4); client.Batches.Should().Be(3);
        status.Count(value => value == "Deferred").Should().Be(2);
        status.Count(value => value == "Validated").Should().Be(4);
    }
    private sealed class MissingClient : ILocalAiInferenceClient
    {
        public int Singles, Batches;
        public Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken ct = default)
        {
            bool batch = request.BatchSpanCounts is not null;
            if (batch) Batches++; else Singles++;
            return Task.FromResult(new LocalAiInferenceResult(batch ? "{\"items\":[]}" : "{\"index\":0}", null,
                "stop", ModelId: LocalAiModelIds.Qwen, Attempted: true));
        }
    }
    private sealed class Client : ILocalAiInferenceClient
    {
        public int Calls { get; private set; }
        public LocalAiInferenceRequest? Last { get; private set; }
        public Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken ct = default)
        {
            Calls++; Last = request;
            string text = request.BatchSpanCounts is null ? "{\"index\":0}" : JsonSerializer.Serialize(new
            { items = request.BatchSpanCounts.Keys.Reverse().Select(id => new { id, result = new { index = 0 } }) });
            return Task.FromResult(new LocalAiInferenceResult(text, null, "stop", ModelId: LocalAiModelIds.Qwen, Attempted: true));
        }
    }
}
