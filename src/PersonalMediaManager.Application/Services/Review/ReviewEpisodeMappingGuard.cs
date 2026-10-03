using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Services.Review;

/// <summary>用完整逐集目录校验累计编号映射</summary>
public static class ReviewEpisodeMappingGuard
{
    public sealed record Result(int? Episode, int? EpisodeEnd, int Offset,
        IReadOnlyList<string> Evidence, string? Error, string? CatalogueFingerprint);

    /// <summary>只映射用户指定目标季，不将季摘要集数直接当偏移</summary>
    public static Result Map(int tmdbId, int targetSeason, int sourceEpisode, int? sourceEnd,
        TmdbDetailsResult details, IReadOnlyList<TmdbSeasonCatalogueResult> catalogues, DateTimeOffset now)
    {
        Result Fail(string message) => new(null, null, 0, [], message, null);
        if (details.TmdbId != tmdbId || details.MediaType != "tv") return Fail("作品目录身份不一致，请重新选择同一 TV 作品");
        if (targetSeason is < 2 or > 100) return Fail("累计编号换算仅支持第 2 至 100 季；特别篇请手动核对");
        if (sourceEpisode < 1 || sourceEnd is int last && last < sourceEpisode) return Fail("原编号范围无效");
        if (!string.IsNullOrEmpty(details.RefreshError)) return Fail("作品目录刷新失败，暂不能确认编号映射");
        if (details.Seasons is not { Count: > 0 }
            || details.Seasons.GroupBy(s => s.SeasonNumber).Any(g => g.Count() != 1))
            return Fail("季目录缺失或冲突，不能仅凭总季数换算");
        int offset = 0;
        List<string> evidence = [$"已核对作品 tv:{tmdbId}；累计序不包含特别篇"];
        List<object> signature = [];
        for (int number = 1; number <= targetSeason; number++)
        {
            TmdbSeasonInfo? summary = details.Seasons.SingleOrDefault(s => s.SeasonNumber == number);
            TmdbSeasonCatalogueResult[] matches = catalogues.Where(s => s.Season?.SeasonNumber == number).ToArray();
            if (summary is null || summary.EpisodeCount is < 1 or > 10000 || matches.Length != 1)
                return Fail($"第 {number} 季目录不完整，不能计算累计偏移");
            TmdbSeasonCatalogueResult catalogue = matches[0];
            if (catalogue.TmdbId != tmdbId || catalogue.MediaType != "tv" || catalogue.CachedAt is null
                || !string.IsNullOrEmpty(catalogue.RefreshError))
                return Fail($"第 {number} 季目录身份、时间或刷新状态无法核实");
            TmdbEpisodeRef[] episodes = catalogue.Season!.Episodes.OrderBy(e => e.EpisodeNumber).ToArray();
            if (episodes.Length != summary.EpisodeCount
                || !episodes.Select(e => e.EpisodeNumber).SequenceEqual(Enumerable.Range(1, summary.EpisodeCount)))
                return Fail($"第 {number} 季逐集目录与摘要不一致或含缺集，不能换算");
            if (number < targetSeason && episodes.Any(e => e.AirDate is null || e.AirDate > now))
                return Fail($"第 {number} 季含未播集或播出时间未知，前季累计范围尚不能确认");
            signature.Add(new { season = number, episodes = episodes.Select(e => new { e.EpisodeNumber, e.AirDate }) });
            evidence.Add($"第 {number} 季已核对 {episodes.Length} 个逐集条目，缓存 {catalogue.CachedAt:O}");
            if (number < targetSeason) offset = checked(offset + episodes.Length);
        }
        int target = sourceEpisode - offset;
        int? targetEnd = sourceEnd is int end ? end - offset : null;
        int count = details.Seasons.Single(s => s.SeasonNumber == targetSeason).EpisodeCount;
        if (target < 1 || target > count || targetEnd is int mappedEnd && (mappedEnd < target || mappedEnd > count))
            return Fail("原编号不在目标季的完整逐集目录内，或文件范围跨季；请手动核对");
        evidence.Add($"前 {targetSeason - 1} 季逐集目录合计 {offset} 集；{sourceEpisode} → S{targetSeason:00}E{target:00}");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signature))));
        return new(target, targetEnd, offset, evidence, null, fingerprint);
    }
}
