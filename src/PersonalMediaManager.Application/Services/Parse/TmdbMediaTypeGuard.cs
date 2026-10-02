using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>区分解析类型和已选 TMDB 身份类型；显式冲突不以删除季集来掩盖。</summary>
public static class TmdbMediaTypeGuard
{
    public static TmdbMediaTypeDecision Reconcile(RuleParseResult rule, AiParseResult? ai,
        string candidateType, IEnumerable<string> sourceNames, bool forcedMatch = false)
    {
        string extracted = Normalize(ai?.MediaType ?? rule.MediaType);
        string resolved = Normalize(candidateType);
        if (resolved == "unknown") return new(extracted, resolved, "CandidateTypeUnknown", true);
        if (forcedMatch) return new(extracted, resolved, "ManualIdentityType", false);
        if (rule.ForceType && Normalize(rule.MediaType) is not "unknown" && Normalize(rule.MediaType) != resolved)
            return new(extracted, resolved, "ForcedTypeConflict", true);
        string[] names = sourceNames.ToArray();
        bool episodic = rule.Season.HasValue || rule.Episode.HasValue || rule.EpisodeEnd.HasValue
            || ai?.Season is not null || ai?.Episode is not null || ai?.EpisodeEnd is not null
            || AiParseResultGuard.HasEpisodicSourceEvidence(names);
        if (resolved == "movie" && episodic) return new(extracted, resolved, "EpisodicFieldsTypeConflict", true);
        // 明确发行形式词才算电影证据；年份、BDRip、动画题材和一般标题里的 film 不算。
        bool movieMarker = names.Length > 0 && MediaTypeEvidence.HasMovieSupport(
            new(names[0], RelativeSegments: names.Skip(1).ToArray()));
        if (resolved == "tv" && movieMarker) return new(extracted, resolved, "MovieMarkerTypeConflict", true);
        return new(extracted, resolved, extracted == resolved ? "CandidateTypeConfirmed"
            : extracted == "unknown" ? "CandidateTypeResolved" : "InferredTypeCorrected", false);
    }

    private static string Normalize(string? type) => type?.ToLowerInvariant() is "movie" ? "movie"
        : type?.ToLowerInvariant() is "tv" ? "tv" : "unknown";
}

public sealed record TmdbMediaTypeDecision(string ExtractedType, string ResolvedType, string ReasonCode, bool RequiresReview);
