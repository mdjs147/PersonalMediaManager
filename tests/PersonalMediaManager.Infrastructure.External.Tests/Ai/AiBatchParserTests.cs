using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.Ai;
namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;

public sealed class AiBatchParserTests
{
    private static readonly AiProviderEndpoint Endpoint = new("https://unused.invalid", "test-secret", "test-model");
    [Fact]
    public async Task TwoDifferentFilesUseOnePhysicalRequestAndMapReversedIds()
    {
        Stub protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 4 });
        AiParseOutcome?[] result = new AiParseOutcome?[2];
        List<AiTransportScope> usages = [];
        int rpm = 0;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        {
            using AiTransportScope scope = new AiTransportScope(() => rpm++) { ProviderId = 7 }.Enter();
            usages.Add(scope);
            result[i] = await parser.ParseAsync(protocol.Protocol, Endpoint, new(i == 0 ? "Alpha.2020.mkv" : "Beta.2021.mkv"), ct);
        }).ToArray(), new());
        protocol.Requests.Should().HaveCount(1);
        rpm.Should().Be(1);
        usages.Sum(s => s.Usage.Count).Should().Be(1);
        usages.SelectMany(s => s.Usage).Sum(s => s.PromptTokens).Should().Be(11);
        result[0]!.Result.Title.Should().Be("Alpha");
        result[1]!.Result.Title.Should().Be("Beta");
        result[0]!.ResponseText.Should().NotContain("Beta");
        result[1]!.ResponseText.Should().NotContain("Alpha");
        AiTransportScope.Current.Should().BeNull();
    }
    [Fact]
    public async Task DefaultSizeOneKeepsOriginalWirePromptEvenInsidePipeline()
    {
        Stub protocol = new();
        AiProtocolParser parser = new([protocol]);
        AiParseRequest request = new("Alpha.mkv");
        await parser.ParseAsync(protocol.Protocol, Endpoint, request);
        await AiBatchPipeline.RunAsync([async ct => { await parser.ParseAsync(protocol.Protocol, Endpoint, request, ct); }], new());
        protocol.Requests.Should().HaveCount(2);
        protocol.Requests[0].Messages.Should().Equal(protocol.Requests[1].Messages);
        protocol.Requests[1].Messages[1].Content.Should().NotContain("items");
        protocol.Requests[0].MaxTokens.Should().Be(protocol.Requests[1].MaxTokens);
    }
    [Theory]
    [InlineData("missing", 2)]
    [InlineData("invalid-item", 2)]
    [InlineData("duplicate", 3)]
    [InlineData("unknown", 3)]
    [InlineData("borrowed-title", 3)]
    public async Task BrokenItemsFallbackWithoutReplayingValidSiblings(string defect, int calls)
    {
        Stub protocol = new() { Defect = defect };
        AiParseOutcome?[] result = await RunAsync(protocol);
        protocol.Requests.Should().HaveCount(calls);
        result[0]!.Result.Title.Should().Be("Alpha");
        result[1]!.Result.Title.Should().Be("Beta");
    }
    [Fact]
    public async Task DifferentModelsCannotShareRequest()
    {
        Stub protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 4 });
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        { await parser.ParseAsync(protocol.Protocol, Endpoint with { Model = $"model-{i}" }, new("Alpha.mkv"), ct); }).ToArray(), new());
        protocol.Requests.Should().HaveCount(2);
        protocol.Requests.Select(r => r.Endpoint.Model).Should().BeEquivalentTo("model-0", "model-1");
    }
    [Fact]
    public async Task BudgetRejectsUnfitSingleInsteadOfGrowingContext()
    {
        Stub protocol = new();
        Func<Task> run = () => RunAsync(protocol, new() { ContextTokenBudget = 512, ExternalMaxItems = 4 });
        await run.Should().ThrowAsync<AiProviderLogicalException>();
        protocol.Requests.Should().BeEmpty();
    }
    [Fact]
    public async Task RateLimitDoesNotFanOutIntoSingleFallbackRequests()
    {
        Stub protocol = new() { Failure = new AiProviderRateLimitException("fixture") };
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 4 });
        int failures = 0;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(_ => async ct =>
        {
            try { await parser.ParseAsync(protocol.Protocol, Endpoint, new("Alpha.mkv"), ct); }
            catch (AiProviderRateLimitException) { failures++; }
        }).ToArray(), new());
        protocol.Requests.Should().HaveCount(1);
        failures.Should().Be(2);
    }
    [Fact]
    public async Task CancellingOneItemDoesNotCancelSiblingOrAcceptLateResult()
    {
        using CancellationTokenSource one = new();
        Stub protocol = new() { BeforeRespond = () => one.Cancel() };
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 4 });
        bool cancelled = false;
        AiParseOutcome? other = null;
        await AiBatchPipeline.RunAsync([
            async _ => { try { await parser.ParseAsync(protocol.Protocol, Endpoint, new("Alpha.mkv"), one.Token); }
                catch (OperationCanceledException) { cancelled = true; } },
            async ct => { other = await parser.ParseAsync(protocol.Protocol, Endpoint, new("Beta.mkv"), ct); }
        ], new());
        cancelled.Should().BeTrue();
        other!.Result.Title.Should().Be("Beta");
        protocol.Requests.Should().HaveCount(1);
    }
    [Fact]
    public async Task TimelyResponseDoesNotExpireWhileWaitingForEarlierBusinessContinuation()
    {
        Stub protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 2 });
        AiParseOutcome? second = null;
        await AiBatchPipeline.RunAsync([
            async ct =>
            {
                await parser.ParseAsync(protocol.Protocol, Endpoint with { TimeoutSeconds = 1 }, new("Alpha.mkv"), ct);
                await Task.Delay(1200, ct);
            },
            async ct => { second = await parser.ParseAsync(protocol.Protocol, Endpoint with { TimeoutSeconds = 1 }, new("Beta.mkv"), ct); }
        ], new());
        second!.Result.Title.Should().Be("Beta");
        protocol.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ModelResponseAfterDeadlineIsStillRejected()
    {
        LateProtocol protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 2 });
        int rejected = 0;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(_ => async ct =>
        {
            try { await parser.ParseAsync(protocol.Protocol, Endpoint with { TimeoutSeconds = 1 }, new("Alpha.mkv"), ct); }
            catch (AiProviderLogicalException) { rejected++; }
        }).ToArray(), new());
        rejected.Should().Be(2);
    }

    [Fact]
    public async Task UserCancellationAfterResponseBeforeReleaseStillWins()
    {
        Stub protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 2 });
        using CancellationTokenSource secondUser = new();
        bool cancelled = false;
        await AiBatchPipeline.RunAsync([
            async ct =>
            {
                await parser.ParseAsync(protocol.Protocol, Endpoint, new("Alpha.mkv"), ct);
                secondUser.Cancel();
                await Task.Delay(10, ct);
            },
            async _ =>
            {
                try { await parser.ParseAsync(protocol.Protocol, Endpoint, new("Beta.mkv"), secondUser.Token); }
                catch (OperationCanceledException) { cancelled = true; }
            }
        ], new());
        cancelled.Should().BeTrue();
        protocol.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ValidSiblingIsReadyBeforeSlowInvalidFirstItemFallback()
    {
        SlowFallbackProtocol protocol = new();
        AiProtocolParser parser = new([protocol], new() { ExternalMaxItems = 2 });
        AiParseOutcome? second = null;
        await AiBatchPipeline.RunAsync([
            async ct => { try { await parser.ParseAsync(protocol.Protocol, Endpoint with { TimeoutSeconds = 1 }, new("Alpha.mkv"), ct); }
                catch (AiProviderLogicalException) { } },
            async ct => { second = await parser.ParseAsync(protocol.Protocol, Endpoint with { TimeoutSeconds = 1 }, new("Beta.mkv"), ct); }
        ], new());
        second!.Result.Title.Should().Be("Beta");
        protocol.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData("chunks")]
        [InlineData("fallback")]
    public async Task PhysicalQuotaIsCheckedForEveryChunkBudgetSplitAndFallback(string mode)
    {
        Stub protocol = new() { Defect = mode == "fallback" ? "missing" : null };
        AiBatchOptions options = new() { ExternalMaxItems = 2, ContextTokenBudget = mode == "budget" ? 512 : 32768 };
        AiProtocolParser parser = new([protocol], options);
        int remaining = 1, settled = 0, tokens = 0;
        int count = mode == "chunks" ? 4 : 2;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, count).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        {
            using AiTransportScope scope = new AiTransportScope(_ =>
            {
                if (remaining == 0) throw new AiProviderRateLimitException("合成额度用尽");
                remaining--; return Task.CompletedTask;
            }, usage => { settled++; tokens += (usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0); return Task.CompletedTask; })
                { ProviderId = 7 }.Enter();
            try { await parser.ParseAsync(protocol.Protocol, Endpoint, new(i % 2 == 0 ? "Alpha.mkv" : "Beta.mkv"), ct); }
            catch (AiProviderRateLimitException) { }
        }).ToArray(), options);
        protocol.Requests.Should().HaveCount(1);
        settled.Should().Be(1);
        tokens.Should().Be(18);
    }

    private sealed class SlowFallbackProtocol : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public int Calls;
        public async Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (Calls > 1) { await Task.Delay(1500); return new("{}"); }
            using JsonDocument doc = JsonDocument.Parse(request.Messages[1].Content);
            string id = doc.RootElement.GetProperty("items")[1].GetProperty("id").GetString()!;
            return new(JsonSerializer.Serialize(new { items = new[] { new { id, result = new { title = "Beta", type = "movie", confidence = 0.9 } } } }));
        }
    }

    private sealed class LateProtocol : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public async Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            // 模拟不及时响应取消的供应商，返回晚到数据仍须拒绝。
            await Task.Delay(1200);
            return await new Stub().CompleteAsync(request, ct);
        }
    }

    private static async Task<AiParseOutcome?[]> RunAsync(Stub protocol, AiBatchOptions? options = null)
    {
        AiProtocolParser parser = new([protocol], options ?? new() { ExternalMaxItems = 4 });
        AiParseOutcome?[] results = new AiParseOutcome?[2];
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        { results[i] = await parser.ParseAsync(protocol.Protocol, Endpoint, new(i == 0 ? "Alpha.2020.mkv" : "Beta.2021.mkv"), ct); }).ToArray(), options ?? new());
        return results;
    }
    private sealed class Stub : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public List<AiProtocolRequest> Requests { get; } = [];
        public string? Defect { get; init; }
        public Exception? Failure { get; init; }
        public Action? BeforeRespond { get; init; }
        public Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            BeforeRespond?.Invoke();
            string text = request.Messages[1].Content;
            if (!text.StartsWith('{')) return Task.FromResult(new AiCompletion(Result(text).GetRawText(), 11, 7));
            using JsonDocument doc = JsonDocument.Parse(text);
            List<(string Id, JsonElement Result)> rows = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => (e.GetProperty("id").GetString()!, Result(e.GetProperty("input").GetString()!))).ToList();
            if (Defect == "missing") rows.RemoveAt(1);
            if (Defect == "duplicate") rows.Add(rows[0]);
            if (Defect == "unknown") rows[0] = ("unknown-id", rows[0].Result);
            if (Defect == "invalid-item") rows[1] = (rows[1].Id, JsonSerializer.SerializeToElement(new { title = 42 }));
            if (Defect == "borrowed-title") (rows[0], rows[1]) = ((rows[0].Id, rows[1].Result), (rows[1].Id, rows[0].Result));
            return Task.FromResult(new AiCompletion(JsonSerializer.Serialize(new
            { items = rows.AsEnumerable().Reverse().Select(r => new { id = r.Id, result = r.Result }) }), 11, 7));
        }
        private static JsonElement Result(string input) => JsonSerializer.SerializeToElement(new
        { title = input.Contains("Alpha", StringComparison.Ordinal) ? "Alpha" : "Beta", type = "movie", confidence = 0.9 });
    }
}
