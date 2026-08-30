using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Tests.Tmdb;

/// <summary>TmdbClient 富化解析 — append_to_response 详情 + 单季分集</summary>
public sealed class TmdbEnrichedParseTests
{
    private const string MovieEnriched = """
        {
          "id":27205,"title":"盗梦空间","original_title":"Inception","release_date":"2010-07-16",
          "overview":"梦境","tagline":"你的思想是犯罪现场","poster_path":"/p.jpg","backdrop_path":"/b.jpg",
          "runtime":148,"vote_average":8.4,"vote_count":34000,"status":"Released","original_language":"en",
          "origin_country":["US"],"homepage":"http://x",
          "genres":[{"id":28,"name":"动作"},{"id":878,"name":"科幻"}],
          "production_companies":[{"id":923,"name":"Legendary","logo_path":"/l.png","origin_country":"US"}],
          "credits":{
            "cast":[{"id":6193,"name":"莱昂纳多","profile_path":"/leo.jpg","character":"Cobb","order":0,"known_for_department":"Acting"}],
            "crew":[
              {"id":525,"name":"克里斯托弗·诺兰","job":"Director","department":"Directing","profile_path":"/n.jpg"},
              {"id":999,"name":"路人","job":"Foley Artist","department":"Sound"}
            ]
          },
          "keywords":{"keywords":[{"id":1,"name":"dream"},{"id":2,"name":"heist"}]}
        }
        """;

    private const string TvSeason = """
        {
          "season_number":1,"name":"第 1 季","overview":"S1 概述","poster_path":"/sp.jpg","air_date":"2008-01-20",
          "episodes":[
            {"episode_number":1,"name":"试播集","overview":"E1 剧情","still_path":"/s1.jpg","air_date":"2008-01-20","runtime":58,"vote_average":8.2},
            {"episode_number":2,"name":"猫","overview":"E2 剧情","still_path":"/s2.jpg","air_date":"2008-01-27","runtime":48,"vote_average":8.0}
          ]
        }
        """;

    private static TmdbClient NewClient(StubHttpMessageHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<TmdbClient>.Instance, new TokenBucketRateLimiter(1000, TimeSpan.FromMilliseconds(50)));

    [Fact(DisplayName = "富化电影详情：标量 + 类型 + 公司 + 演职员 + 关键词全解析")]
    public async Task EnrichedMovie_Parses_All_Sections()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, MovieEnriched);

        TmdbClient client = NewClient(h);
        TmdbEnrichedDetails d = await client.GetEnrichedDetailsAsync(27205, "movie", "key");

        d.Title.Should().Be("盗梦空间");
        d.Tagline.Should().Be("你的思想是犯罪现场");
        d.Runtime.Should().Be(148);
        d.VoteAverage.Should().Be(8.4);
        d.BackdropPath.Should().Be("/b.jpg");
        d.Year.Should().Be(2010);
        d.Genres.Should().HaveCount(2);
        d.Companies.Should().ContainSingle().Which.Name.Should().Be("Legendary");
        d.Cast.Should().ContainSingle().Which.Character.Should().Be("Cobb");
        // crew 仅保留白名单职务：Director 入，Foley Artist 被过滤
        d.Crew.Should().ContainSingle().Which.Job.Should().Be("Director");
        d.Keywords.Should().HaveCount(2);
        // append_to_response 已带上
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("append_to_response=credits");
    }

    [Fact(DisplayName = "单季分集：每集名/简介/剧照/时长解析")]
    public async Task Season_Parses_Episodes()
    {
        StubHttpMessageHandler h = new();
        h.EnqueueResponse(HttpStatusCode.OK, TvSeason);

        TmdbClient client = NewClient(h);
        TmdbSeasonDetail s = await client.GetSeasonAsync(1396, 1, "key");

        s.SeasonNumber.Should().Be(1);
        s.Episodes.Should().HaveCount(2);
        s.Episodes[0].Name.Should().Be("试播集");
        s.Episodes[0].Overview.Should().Be("E1 剧情");
        s.Episodes[0].Runtime.Should().Be(58);
        h.Requests[0].RequestUri!.AbsoluteUri.Should().Contain("/tv/1396/season/1");
    }
}
