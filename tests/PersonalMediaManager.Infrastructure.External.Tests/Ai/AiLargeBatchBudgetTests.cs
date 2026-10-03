using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.Ai;
namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class AiLargeBatchBudgetTests
{
    [Theory]
    [InlineData("null", 2, 128, 0)]
    [InlineData("missing", 2, 128, 0)]
    [InlineData("unknown", 5, 4, 124)]
    [InlineData("duplicate", 5, 4, 124)]
    [InlineData("truncated", 5, 4, 124)]
    [InlineData("length", 5, 4, 124)]
    public async Task LargeEnvelopeHasStrictIdsAndOnlyFourAutomaticFallbacks(string defect, int calls, int successes, int deferred)
    {
        ProtocolStub protocol = new() { Defect = defect };
        AiBatchOptions budget = new() { MaxItems = 128, ExternalMaxItems = 128, ContextTokenBudget = 1048576,
            MaxOutputTokens = 262144, MaxResponseBytes = 8388608, MaxWaitMilliseconds = 1000 };
        AiProtocolParser parser = new([protocol], budget);
        int valid = 0, postponed = 0;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 128).Select<int, Func<CancellationToken, Task>>(index => async ct =>
        {
            try
            {
                AiParseOutcome result = await parser.ParseAsync(protocol.Protocol, new("https://fixture.invalid", null, "fixture"), new($"Work{index:D3}.mkv"), ct);
                result.Result.Title.Should().Be($"Work{index:D3}"); valid++;
            }
            catch (AiProviderBatchDeferredException) { postponed++; }
        }).ToArray(), budget);
        protocol.Calls.Should().Be(calls); valid.Should().Be(successes); postponed.Should().Be(deferred);
    }

    [Fact]
    public async Task CrossItemTypeYearSeasonAreNotAcceptedFromValidJson()
    {
        ProtocolStub protocol = new() { Defect = "pollution" };
        AiBatchOptions budget = new() { MaxItems = 128, ExternalMaxItems = 128, ContextTokenBudget = 1048576,
            MaxOutputTokens = 262144, MaxResponseBytes = 8388608, MaxWaitMilliseconds = 1000 };
        AiProtocolParser parser = new([protocol], budget);
        AiParseOutcome?[] results = new AiParseOutcome?[128];
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 128).Select<int, Func<CancellationToken, Task>>(index => async ct =>
        {
            results[index] = await parser.ParseAsync(protocol.Protocol, new("https://fixture.invalid", null, "fixture"),
                new($"Work{index:D3}.S02E03.mkv", Context: new(SchemaVersion: 2)), ct);
        }).ToArray(), budget);
        protocol.Calls.Should().Be(1);
        results[0]!.Result.Abstained.Should().BeTrue();
        results[0]!.Result.Year.Should().BeNull();
        results[0]!.Result.Season.Should().NotBe(9);
        results[1]!.Result.MediaType.Should().Be("tv"); results[1]!.Result.Season.Should().Be(2);
        results[1]!.Result.SelectedCandidateId.Should().BeNull();
        results[0]!.Result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task OversizedSingleNeverEscapesByBudgetFallback()
    {
        ProtocolStub protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 128, ContextTokenBudget = 512 });
        Func<Task> action = () => AiBatchPipeline.RunAsync([async ct =>
            { await parser.ParseAsync(protocol.Protocol, new("https://fixture.invalid", null, "fixture"), new("Work000.mkv"), ct); }], new());
        await action.Should().ThrowAsync<AiProviderLogicalException>(); protocol.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResponseReaderUsesConfiguredEnvelopeLimitAndAbsoluteEightMiB()
    {
        using StringContent allowed = new(new string('x', 1048577));
        (await AiResponseReader.ReadAsync(allowed, 2097152, default)).Length.Should().Be(1048577);
        using StringContent oversized = new(new string('x', 8388609));
        Func<Task> action = () => AiResponseReader.ReadAsync(oversized, int.MaxValue, default);
        await action.Should().ThrowAsync<AiProviderLogicalException>();
    }

    private sealed class ProtocolStub : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public string? Defect { get; init; }
        public int Calls;
        public Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            Calls++;
            string text = request.Messages[1].Content;
            if (!text.StartsWith('{')) return Task.FromResult(new AiCompletion(JsonSerializer.Serialize(Result(text))));
            using JsonDocument doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items))
                return Task.FromResult(new AiCompletion(JsonSerializer.Serialize(Result(text))));
            List<(string Id, object? Result)> rows = items.EnumerateArray().Select(row =>
                (row.GetProperty("id").GetString()!, (object?)Result(row.GetProperty("input").GetString()!))).ToList();
            if (Defect == "null") rows[0] = (rows[0].Id, null);
            if (Defect == "missing") rows.RemoveAt(0);
            if (Defect == "unknown") rows[0] = ("invented", rows[0].Result);
            if (Defect == "duplicate") rows.Add(rows[0]);
            if (Defect == "pollution") rows[0] = (rows[0].Id, new { title = "Work000", type = "movie", year = 2025, season = 9, episode = 3, confidence = 0.95 });
            string result = JsonSerializer.Serialize(new { items = rows.AsEnumerable().Reverse().Select(row => new { id = row.Id, result = row.Result }) });
            return Task.FromResult(new AiCompletion(Defect == "truncated" ? result[..^10] : result, FinishReason: Defect == "length" ? "length" : null));
        }
        private static object Result(string input)
        {
            int offset = input.IndexOf("Work", StringComparison.Ordinal);
            string title = input.Substring(offset, 7);
            return new { title, type = input.Contains("S02E03", StringComparison.Ordinal) ? "tv" : "movie",
                year = (int?)null, season = input.Contains("S02E03", StringComparison.Ordinal) ? (int?)2 : null,
                episode = input.Contains("S02E03", StringComparison.Ordinal) ? (int?)3 : null, confidence = 0.9 };
        }
    }
}
