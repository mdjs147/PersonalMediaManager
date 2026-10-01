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
            : info is null ? "SeasonAbsent"
            : info.EpisodeCount <= 0 ? "EpisodeCountUnknown"
            : episode < 1 || episode > info.EpisodeCount
                || (episodeEnd is int end && (end < episode || end > info.EpisodeCount))
                ? "EpisodeOutsideCatalogue" : null;
    }
}
