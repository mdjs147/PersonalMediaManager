using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Tmdb;

/// <summary>TmdbClient：候选解析 + 429 退避/上限钳制 + 500 抛异常 + 限流速率注入 + 语言兜底</summary>
public sealed class TmdbClientTests
{
    private const string SearchSingle = """
        {"results":[{"id":27205,"title":"Inception","original_title":"Inception","release_date":"2010-07-16","popularity":50.5,"original_language":"en","origin_country":["US"],"poster_path":"/poster.jpg","overview":"X"}],"total_results":1}
        """;
    private const string SearchThree = """
        {"results":[
          {"id":1,"title":"A","release_date":"2020-01-01","popularity":10,"original_language":"en"},
          {"id":2,"title":"B","release_date":"2020-01-01","popularity":20,"original_language":"en"},
          {"id":3,"title":"C","release_date":"2020-01-01","popularity":30,"original_language":"en"}
        ]}
        """;
    private const string SearchFive = """
        {"results":[
          {"id":1,"title":"A","release_date":"2020-01-01","popularity":1,"original_language":"en"},
          {"id":2,"title":"B","release_date":"2020-01-01","popularity":2,"original_language":"en"},
          {"id":3,"title":"C","release_date":"2020-01-01","popularity":3,"original_language":"en"},
          {"id":4,"title":"D","release_date":"2020-01-01","popularity":4,"original_language":"en"},
          {"id":5,"title":"E","release_date":"2020-01-01","popularity":5,"original_language":"en"}
        ]}
        """;

    private static TmdbClient NewClient(StubHttpMessageHandler handler)
    {
        StubHttpClientFactory factory = new(handler);
        // 速率限制：测试用 1000/s 等价于不节流，避免单测耗时
        return new TmdbClient(factory, NullLogger<TmdbClient>.Instance, new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Search_SingleCandidate_Returns1()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchSingle);

        TmdbClient client = NewClient(h);
        TmdbSearchResult res = await client.SearchAsync(new TmdbSearchRequest("Inception", "movie", 2010), "key");

        res.Candidates.Should().HaveCount(1);
        res.Candidates[0].Id.Should().Be(27205);
        res.Candidates[0].Title.Should().Be("Inception");
        res.Candidates[0].Year.Should().Be(2010);
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("year=2010");
    }

    [Fact]
    public async Task Search_ThreeCandidates_AllParsed()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchThree);

        TmdbClient client = NewClient(h);
        TmdbSearchResult res = await client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key");
        res.Candidates.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_MoreThanThree_AllReturned_RankerDecidesLater()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchFive);

        TmdbClient client = NewClient(h);
        TmdbSearchResult res = await client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key");
        res.Candidates.Should().HaveCount(5); // 客户端不裁剪，调用方按 N 阈值决策
    }

    [Fact]
    public async Task Search_429WithRetryAfter_RetriesThenSucceeds()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.TooManyRequests, "{}", headers => headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero));
        h.EnqueueResponse(HttpStatusCode.OK, SearchSingle);

        TmdbClient client = NewClient(h);
        TmdbSearchResult res = await client.SearchAsync(new TmdbSearchRequest("Inception", "movie"), "key");
        res.Candidates.Should().HaveCount(1);
        h.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_500_ThrowsTmdbClientException()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.InternalServerError, """{"status_message":"boom"}""");

        TmdbClient client = NewClient(h);
        Func<Task> act = () => client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key");
        TmdbClientException ex = (await act.Should().ThrowAsync<TmdbClientException>()).Which;
        ex.HttpStatus.Should().Be(500);
    }

    [Fact]
    public async Task Search_RetryExhausted_ThrowsTmdbClientException()
    {
        StubHttpMessageHandler h = new();
        // 4 次 429（初始 + 3 次重试，第 4 次直接抛）
        for (int i = 0; i < 4; i++)
            h.EnqueueResponse(HttpStatusCode.TooManyRequests, "{}", headers => headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero));

        TmdbClient client = NewClient(h);
        Func<Task> act = () => client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key");
        TmdbClientException ex = (await act.Should().ThrowAsync<TmdbClientException>()).Which;
        ex.HttpStatus.Should().Be(429);
        h.Requests.Should().HaveCount(4);
    }

    // ── 429 Retry-After 上限钳制（修复：原样信任服务端 3600s 会堵死串行管线数小时）──

    /// <summary>构造可记录退避等待的客户端（等待委托替换为记录器，不真实等待，单测秒过）</summary>
    private static (TmdbClient Client, List<TimeSpan> Delays) NewClientWithDelayRecorder(StubHttpMessageHandler handler)
    {
        List<TimeSpan> delays = [];
        TmdbClient client = new(
            new StubHttpClientFactory(handler),
            NullLogger<TmdbClient>.Instance,
            new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        return (client, delays);
    }

    [Fact]
    public async Task Search_RetryAfter3600_ThrowsImmediatelyWithoutWaiting()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.TooManyRequests, "{}",
            headers => headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3600)));

        (TmdbClient client, List<TimeSpan> delays) = NewClientWithDelayRecorder(h);
        Func<Task> act = () => client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key");

        TmdbClientException ex = (await act.Should().ThrowAsync<TmdbClientException>()).Which;
        ex.Message.Should().Contain("限流等待过长").And.Contain("3600");
        ex.HttpStatus.Should().Be(429);
        h.Requests.Should().HaveCount(1, "超上限应立即放弃，不再重试");
        delays.Should().BeEmpty("超上限不应进入等待");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)] // 边界：等于上限 60s 仍属可等待范围
    public async Task Search_RetryAfterWithinCap_WaitsThenSucceeds(int retryAfterSeconds)
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.TooManyRequests, "{}",
            headers => headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds)));
        h.EnqueueResponse(HttpStatusCode.OK, SearchSingle);

        (TmdbClient client, List<TimeSpan> delays) = NewClientWithDelayRecorder(h);
        TmdbSearchResult res = await client.SearchAsync(new TmdbSearchRequest("Inception", "movie"), "key");

        res.Candidates.Should().HaveCount(1);
        h.Requests.Should().HaveCount(2);
        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(retryAfterSeconds));
    }

    // ── 限流速率随设置流入（修复：旧版硬编码 40，Tmdb_Setting.RateLimitPerSecond 是死旋钮）──

    [Fact]
    public async Task Search_RateLimitParameter_SwapsLimiterOnChangeAndKeepsOnSameOrNull()
    {
        StubHttpMessageHandler h = new();
        for (int i = 0; i < 4; i++) h.EnqueueResponse(HttpStatusCode.OK, SearchSingle);

        // 公共构造 → 默认 40 req/s
        TmdbClient client = new(new StubHttpClientFactory(h), NullLogger<TmdbClient>.Instance);
        client.CurrentRateLimitPerSecond.Should().Be(40);

        // 显式传新速率 → 换桶
        await client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key", rateLimitPerSecond: 10);
        client.CurrentRateLimitPerSecond.Should().Be(10);
        TokenBucketRateLimiter limiterAt10 = client.CurrentRateLimiter;

        // 同速率 → 不换桶（避免清空已积累令牌）
        await client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key", rateLimitPerSecond: 10);
        client.CurrentRateLimiter.Should().BeSameAs(limiterAt10);

        // 未传（null）→ 沿用当前
        await client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key");
        client.CurrentRateLimitPerSecond.Should().Be(10);
        client.CurrentRateLimiter.Should().BeSameAs(limiterAt10);

        // 再次变更 → 换新桶
        await client.SearchAsync(new TmdbSearchRequest("X", "movie"), "key", rateLimitPerSecond: 25);
        client.CurrentRateLimitPerSecond.Should().Be(25);
        client.CurrentRateLimiter.Should().NotBeSameAs(limiterAt10);
    }

    [Fact]
    public async Task GetDetails_RateLimitParameter_TakesEffect()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, """{"id":27205,"title":"Inception","release_date":"2010-07-16"}""");

        TmdbClient client = new(new StubHttpClientFactory(h), NullLogger<TmdbClient>.Instance);
        await client.GetDetailsAsync(27205, "movie", "key", "zh-CN", rateLimitPerSecond: 5);
        client.CurrentRateLimitPerSecond.Should().Be(5);
    }

    // ── 语言参数：请求未指定（null）时按 zh-CN 兜底，与旧版 record 默认行为一致 ──

    [Fact]
    public async Task Search_NoLanguageSpecified_DefaultsToZhCn()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchSingle);

        TmdbClient client = NewClient(h);
        await client.SearchAsync(new TmdbSearchRequest("Inception", "movie"), "key");
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("language=zh-CN");
    }

    [Fact]
    public async Task Search_ExplicitLanguage_PassedThrough()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, SearchSingle);

        TmdbClient client = NewClient(h);
        await client.SearchAsync(new TmdbSearchRequest("Inception", "movie", Language: "en-US"), "key");
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("language=en-US");
    }
}
