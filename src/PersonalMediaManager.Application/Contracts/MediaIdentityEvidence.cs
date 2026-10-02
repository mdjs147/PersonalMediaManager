namespace PersonalMediaManager.Application.Contracts;

/// <summary>同名身份不能仅靠语言或热度分差消歧</summary>
public static class MediaIdentityEvidence
{
    public static bool HasUnresolvedNamesake(AiCandidateEvidence selected,
        IEnumerable<AiCandidateEvidence> candidates, IEnumerable<string> sourceNames)
    {
        string Key(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        HashSet<string> names = new[] { selected.Title, selected.OriginalTitle ?? "" }.Select(Key)
            .Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
        AiCandidateEvidence[] sameNames = candidates.Where(candidate => names.Contains(Key(candidate.Title))
            || candidate.OriginalTitle is { Length: > 0 } other && names.Contains(Key(other))).ToArray();
        if (sameNames.Select(candidate => (candidate.TmdbId, candidate.MediaType)).Distinct().Count() <= 1) return false;
        return selected.Year is not int year || !MediaYearEvidence.ContainsYear(sourceNames, year)
            || sameNames.Count(candidate => candidate.Year == year) != 1;
    }
}
