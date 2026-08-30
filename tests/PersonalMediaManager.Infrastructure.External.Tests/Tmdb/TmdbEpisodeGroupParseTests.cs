using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Tmdb;

/// <summary>TmdbClient 剧集组（episode_group）解析 — 编组内 order + 正典 season/episode 映射</summary>
public sealed class TmdbEpisodeGroupParseTests
{
    // 故意让 order ≠ 正典 episode_number（重制版重排）：HD Remaster 第 1 话(order 0)→正典 S01E02，
    // 第 2 话(order 1)→S01E01，第 3 话(order 2)→S01E04，证明解析保留的是"编组内位置 → 正典季集"的真实映射。
    private const string EpisodeGroupJson = """
        {
          "id":"5deddbdcdc86470011c4bb7d","name":"HD Remaster","type":6,"group_count":1,"episode_count":3,
          "groups":[
            {
              "id":"5deddbfedaf57c0015ed4e0a","name":"HD Remaster","order":1,"locked":true,
              "episodes":[
                {"id":1001,"name":"假面的下方","season_number":1,"episode_number":2,"order":0,"air_date":"2011-10-22"},
                {"id":1002,"name":"崩溃的大地","season_number":1,"episode_number":1,"order":1},
                {"id":1003,"name":"消失的GUNDAM","season_number":1,"episode_number":4,"order":2}
              ]
            }
          ]
        }
        """;

    private static TmdbClient NewClient(StubHttpMessageHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<TmdbClient>.Instance, new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));

    [Fact(DisplayName = "剧集组：分组 + 编组内 order → 正典季集映射全解析")]
    public async Task EpisodeGroup_Parses_Groups_And_CanonicalMapping()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, EpisodeGroupJson);

        TmdbClient client = NewClient(h);
        TmdbEpisodeGroup g = await client.GetEpisodeGroupAsync("5deddbdcdc86470011c4bb7d", "key");

        g.Id.Should().Be("5deddbdcdc86470011c4bb7d");
        g.Name.Should().Be("HD Remaster");
        g.Type.Should().Be(6);
        g.Groups.Should().ContainSingle();

        TmdbEpisodeGroupSegment seg = g.Groups[0];
        seg.Id.Should().Be("5deddbfedaf57c0015ed4e0a");
        seg.Order.Should().Be(1);
        seg.Episodes.Should().HaveCount(3);

        // 按 order 取位：第 1 位(order 0)=S01E02，第 2 位(order 1)=S01E01，第 3 位(order 2)=S01E04
        TmdbEpisodeGroupEntry first = seg.Episodes.Single(e => e.Order == 0);
        first.SeasonNumber.Should().Be(1);
        first.EpisodeNumber.Should().Be(2);
        first.Name.Should().Be("假面的下方");
        first.TmdbEpisodeId.Should().Be(1001);

        seg.Episodes.Single(e => e.Order == 1).EpisodeNumber.Should().Be(1);
        seg.Episodes.Single(e => e.Order == 2).EpisodeNumber.Should().Be(4);

        // 端点正确
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("/tv/episode_group/5deddbdcdc86470011c4bb7d");
    }

    [Fact(DisplayName = "剧集组：缺 season_number / episode_number 的条目被跳过，order 缺失回退累计序")]
    public async Task EpisodeGroup_Skips_Incomplete_And_Defaults_Order()
    {
        const string json = """
            {
              "id":"eg1","name":"DVD 序","type":3,
              "groups":[
                {"id":"g1","name":"Part 1","order":1,"episodes":[
                  {"id":1,"name":"有效","season_number":2,"episode_number":5},
                  {"id":2,"name":"缺集号","season_number":2},
                  {"id":3,"name":"也有效","season_number":2,"episode_number":6}
                ]}
              ]
            }
            """;
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, json);

        TmdbEpisodeGroup g = await NewClient(h).GetEpisodeGroupAsync("eg1", "key");

        TmdbEpisodeGroupSegment seg = g.Groups.Single();
        // 缺 episode_number 的条目被跳过：3 条进 2 条
        seg.Episodes.Should().HaveCount(2);
        // order 缺失 → 回退为入列累计序（0、1），保证可按位翻译
        seg.Episodes[0].Order.Should().Be(0);
        seg.Episodes[0].EpisodeNumber.Should().Be(5);
        seg.Episodes[1].Order.Should().Be(1);
        seg.Episodes[1].EpisodeNumber.Should().Be(6);
    }
}
