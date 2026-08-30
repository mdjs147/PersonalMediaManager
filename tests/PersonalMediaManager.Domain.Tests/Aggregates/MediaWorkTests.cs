using PersonalMediaManager.Domain.Aggregates.MediaWorks;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaWork 聚合 — 标量 upsert + 关联/季集原子替换</summary>
public sealed class MediaWorkTests
{
    private static MediaWork New() => MediaWork.CreateMinimal(100, "TV", "繁花", 2023);

    [Fact(DisplayName = "CreateMinimal：MediaType 归一化为小写")]
    public void CreateMinimal_Lowercases_MediaType()
    {
        New().MediaType.Should().Be("tv");
    }

    [Fact(DisplayName = "ReplaceGenres：去重 + 整体替换")]
    public void ReplaceGenres_Dedupes_And_Replaces()
    {
        MediaWork w = New();
        w.ReplaceGenres(new[] { 18, 18, 80 });
        w.Genres.Select(g => g.GenreId).Should().BeEquivalentTo(new[] { 18, 80 });

        w.ReplaceGenres(new[] { 99 });
        w.Genres.Select(g => g.GenreId).Should().Equal(99);
    }

    [Fact(DisplayName = "ReplaceCredits：cast/crew 合一表按 CreditType 区分")]
    public void ReplaceCredits_Splits_Cast_And_Crew()
    {
        MediaWork w = New();
        w.ReplaceCredits(new[]
        {
            new CreditSeed(1, "cast", "角色A", 0, null, null),
            new CreditSeed(2, "crew", null, null, "Director", "Directing"),
        });

        w.Credits.Should().HaveCount(2);
        w.Credits.Single(c => c.CreditType == "cast").Character.Should().Be("角色A");
        w.Credits.Single(c => c.CreditType == "crew").Job.Should().Be("Director");
    }

    [Fact(DisplayName = "ReplaceSeasonEpisodes：仅替换该季，不动其它季")]
    public void ReplaceSeasonEpisodes_Only_Touches_That_Season()
    {
        MediaWork w = New();
        w.ReplaceSeasonEpisodes(1, new[] { new EpisodeSeed(1, 1, "E1", null, null, null, null, null) });
        w.ReplaceSeasonEpisodes(2, new[] { new EpisodeSeed(2, 1, "S2E1", null, null, null, null, null) });
        w.Episodes.Should().HaveCount(2);

        w.ReplaceSeasonEpisodes(1, new[]
        {
            new EpisodeSeed(1, 1, "E1b", null, null, null, null, null),
            new EpisodeSeed(1, 2, "E2", null, null, null, null, null),
        });
        w.Episodes.Count(e => e.SeasonNumber == 1).Should().Be(2);
        w.Episodes.Count(e => e.SeasonNumber == 2).Should().Be(1);
    }

    [Fact(DisplayName = "ClearEpisodes：清空全部分集")]
    public void ClearEpisodes_Empties()
    {
        MediaWork w = New();
        w.ReplaceSeasonEpisodes(1, new[] { new EpisodeSeed(1, 1, "E1", null, null, null, null, null) });
        w.ClearEpisodes();
        w.Episodes.Should().BeEmpty();
    }

    [Fact(DisplayName = "UpsertScalars：写入富化标量")]
    public void UpsertScalars_Sets_Fields()
    {
        MediaWork w = New();
        w.UpsertScalars("标题", "Orig", 2023, "简介", "标语", "/p.jpg", "/b.jpg", 48, 8.5, 100,
            null, "Ended", "zh", new List<string> { "CN" }, null, 3, 30);

        w.VoteAverage.Should().Be(8.5);
        w.TmdbStatus.Should().Be("Ended");
        w.TotalEpisodes.Should().Be(30);
        w.BackdropPath.Should().Be("/b.jpg");
    }

    [Fact(DisplayName = "MarkEnriched：记录富化时刻")]
    public void MarkEnriched_Sets_Timestamp()
    {
        MediaWork w = New();
        w.EnrichedAt.Should().BeNull();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        w.MarkEnriched(at);
        w.EnrichedAt.Should().Be(at);
    }
}
