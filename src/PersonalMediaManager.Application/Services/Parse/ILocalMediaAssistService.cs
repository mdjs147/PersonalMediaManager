using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>本地小模型只提供待核验检索建议</summary>
public interface ILocalMediaAssistService
{
    Task<LocalMediaAssistResult> SuggestAsync(FileParseContext source, RuleParseResult? rule,
        LocalAiMode expectedMode, CancellationToken ct = default);
    Task<LocalMediaLookupResult> LookupAsync(LocalMediaAssistResult suggestions,
        IReadOnlyList<TmdbSearchRequest> alreadyQueried, string language, CancellationToken ct = default);
}

/// <summary>带来源区间的假设；同组别名不证明作品身份</summary>
public sealed record LocalMediaSuggestion(string Title, string Evidence, string Source, int? SegmentIndex,
    int Start, int Length, string HypothesisId, string Kind);

/// <summary>建议审计；不包含可直接采用的季集和身份字段</summary>
public sealed record LocalMediaAssistResult(LocalAiMode Mode, string Status,
    IReadOnlyList<LocalMediaSuggestion> Candidates, IReadOnlyList<string> Reasons,
    string? ModelId = null, string? RawResponse = null, bool FromCache = false,
    bool InferenceAttempted = false, long ElapsedMilliseconds = 0,
    IReadOnlyList<LocalMediaSuggestion>? OfferedCandidates = null, string? ProtocolVersion = null);

/// <summary>有界查询记录；失败与空结果明确分开</summary>
public sealed record LocalMediaQuery(string Title, string MediaType, string Status, int CandidateCount);

/// <summary>只供人工核对的目录候选</summary>
public sealed record LocalMediaLookupResult(IReadOnlyList<TmdbCandidate> Candidates,
    IReadOnlyList<LocalMediaQuery> Queries, bool Incomplete, bool AllFromCache);
