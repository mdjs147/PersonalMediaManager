using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Tmdb;

/// <summary>分季请求的限流、取消和目录完整性</summary>
public sealed class TmdbSeasonCatalogueClientTests
{
    private const string Valid = "{\"season_number\":1,\"episodes\":[{\"episode_number\":1,\"overview\":null}]}";

    [Fact]
    public async Task RateAwareOverloadAppliesSettingAndRetries429()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler()
            .EnqueueResponse(HttpStatusCode.TooManyRequests, "{}", h => h.RetryAfter = new(TimeSpan.Zero))
            .EnqueueResponse(HttpStatusCode.OK, Valid);
        TmdbClient client = new(new StubHttpClientFactory(handler), NullLogger<TmdbClient>.Instance);
        TmdbSeasonDetail result = await client.GetSeasonAsync(42, 1, "测试值", "zh-CN", 7, CancellationToken.None);
        client.CurrentRateLimitPerSecond.Should().Be(7);
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(r => r.RequestUri!.AbsolutePath == "/3/tv/42/season/1");
        result.Episodes.Single().Overview.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"season_number\":2,\"episodes\":[]}")]
    [InlineData("{\"season_number\":1}")]
    [InlineData("{\"season_number\":1,\"episodes\":[{}]}")]
    [InlineData("{\"season_number\":1,\"episodes\":[{\"episode_number\":0}]}")]
    [InlineData("{\"season_number\":1,\"episodes\":[{\"episode_number\":1},{\"episode_number\":1}]}")]
    [InlineData("{\"season_number\":1,\"episodes\":[{\"episode_number\":1,\"season_number\":2}]}")]
    public async Task MalformedSeasonCannotBecomeGoodCache(string response)
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler().EnqueueResponse(HttpStatusCode.OK, response);
        TmdbClient client = new(new StubHttpClientFactory(handler), NullLogger<TmdbClient>.Instance);
        await client.Invoking(c => c.GetSeasonAsync(42, 1, "测试值")).Should().ThrowAsync<TmdbClientException>();
    }

    [Fact]
    public async Task EmptyFutureSeasonIsRepresentedHonestly()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler()
            .EnqueueResponse(HttpStatusCode.OK, "{\"season_number\":1,\"episodes\":[]}");
        TmdbClient client = new(new StubHttpClientFactory(handler), NullLogger<TmdbClient>.Instance);
        (await client.GetSeasonAsync(42, 1, "测试值")).Episodes.Should().BeEmpty();
    }

    [Fact]
    public async Task PrecancelledSeasonDoesNotSendHttp()
    {
        StubHttpMessageHandler handler = new();
        TmdbClient client = new(new StubHttpClientFactory(handler), NullLogger<TmdbClient>.Instance);
        await client.Invoking(c => c.GetSeasonAsync(42, 1, "测试值", "zh-CN", 7, new CancellationToken(true)))
            .Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }
}
