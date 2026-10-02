using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>新补季集的正典目录范围核对（不证明发行编号映射）</summary>
public static class TmdbEpisodeCatalogueGuard
{
    /// <summary>只对本轮新补字段要求目录核对，不重解释既有明确映射</summary>
    public static bool RequiresValidation(RuleParseResult rule, AiParseResult? ai = null) =>
        (rule.FieldEvidence ?? []).Any(e => e.Source != "UserRule" && e.Field is "season" or "episode" or "episodeEnd")
        || (ai?.Validation?.AcceptedFields ?? []).Any(f => f is "season" or "episode" or "episodeEnd");

    /// <summary>返回未知或不一致原因；null 表示目录范围允许</summary>
    public static string? Validate(IReadOnlyList<TmdbSeasonInfo>? seasons, int season, int episode, int? episodeEnd)
    {
        TmdbSeasonInfo? info = seasons?.FirstOrDefault(s => s.SeasonNumber == season);
        return seasons is not { Count: > 0 } ? "CatalogueUnknown"
            : seasons.Count(s => s.SeasonNumber == season) > 1 ? "SeasonCatalogueConflict"
            : info is null ? "SeasonAbsent"
            : info.EpisodeCount <= 0 ? "EpisodeCountUnknown"
            : episode < 1 || episode > info.EpisodeCount
                || (episodeEnd is int end && (end < episode || end > info.EpisodeCount))
                ? "EpisodeOutsideCatalogue" : null;
    }

    /// <summary>仅凭同一 TV 身份的完整单季目录补 S01；汇总季数或搜索候选本身不足以补季。</summary>
    public static string? ValidateSingleSeasonInference(TmdbDetailsResult? details, int expectedTmdbId,
        int episode, int? episodeEnd)
    {
        if (details is null) return "CatalogueUnknown";
        if (details.TmdbId != expectedTmdbId || details.MediaType != "tv") return "CatalogueIdentityMismatch";
        if (details.TotalSeasons is null) return "TotalSeasonsUnknown";
        if (details.TotalSeasons != 1) return "NotSingleSeason";
        if (details.Seasons is not { Count: > 0 }) return "CatalogueUnknown";
        if (details.Seasons.Any(s => s.SeasonNumber < 0)
            || details.Seasons.GroupBy(s => s.SeasonNumber).Any(g => g.Count() > 1))
            return "SeasonCatalogueConflict";
        TmdbSeasonInfo[] regular = details.Seasons.Where(s => s.SeasonNumber > 0).ToArray();
        if (regular.Length != 1) return "SingleSeasonCatalogueMismatch";
        if (regular[0].SeasonNumber != 1) return "FirstSeasonAbsent";
        return Validate(details.Seasons, 1, episode, episodeEnd);
    }
}
