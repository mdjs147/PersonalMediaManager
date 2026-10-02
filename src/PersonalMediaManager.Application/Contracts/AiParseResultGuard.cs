using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>独立于供应商的任务结果安全守护</summary>
public static class AiParseResultGuard
{
    /// <summary>检查原文明确季集结构，供最终身份类型核验共用</summary>
    public static bool HasEpisodicSourceEvidence(IEnumerable<string> sourceNames) =>
        sourceNames.Any(source => HasEpisodicEvidence(new(source, Context: new())));

    /// <summary>单段文本必须支持字段语义，数字碰巧出现不构成事实证据</summary>
    public static bool HasGroundedNumericEvidence(string field, int value, string source) =>
        field == "year" ? MediaYearEvidence.ContainsYear([source], value)
            : !(field is "episode" or "episodeEnd" && HasUnsafeEpisodeRange(source))
                && HasExplicitEvidence(field, value, new(source, Context: new()));

    /// <summary>保护已有字段并拒绝无依据的模型补全</summary>
    public static AiParseResult Validate(AiParseResult result, AiParseRequest request)
    {
        if (request.Context is not { } context) return result;
        List<string> rejected = [.. result.Validation?.RejectedFields ?? []];
        List<string> reasons = [.. result.Validation?.ReasonCodes ?? []];
        List<string> accepted = [];
        if (context.SchemaVersion == 2 && result.Validation?.SchemaIssues?.Any(issue => issue.BlocksAcceptance) == true)
        {
            reasons.Add("InvalidSchema");
            result = result with { Confidence = 0, Abstained = true };
        }
        if (context.SchemaVersion == 2 && (!double.IsFinite(result.Confidence) || result.Confidence is < 0 or > 1))
        {
            rejected.Add("confidence"); reasons.Add("InvalidConfidence");
            result = result with { Confidence = 0, Abstained = true };
        }
        AiLockedBinding? locked = context.LockedBinding;
        if (context.TaskType == AiParseTaskType.FillMissingFields && locked is null)
            throw new AiProviderLogicalException("AI 补字段任务缺少锁定绑定");
        if (context.TaskType == AiParseTaskType.DisambiguateCandidates && !result.Abstained)
        {
            if (locked is not null)
                throw new AiProviderLogicalException("AI 候选消歧任务不能携带锁定绑定");
            AiCandidateEvidence? selected = (context.Candidates ?? []).Take(5).FirstOrDefault(c =>
                c.TmdbId == result.SelectedCandidateId && c.MediaType == result.MediaType && c.MediaType is "tv" or "movie");
            if (selected is null)
                throw new AiProviderLogicalException("AI 未提供请求短表内的候选身份和类型");
            if (context.SchemaVersion == 2 && HasUnresolvedNamesake(selected, request))
            {
                rejected.Add("selectedCandidateId");
                reasons.Add("AmbiguousCandidateIdentity");
                result = result with { SelectedCandidateId = null, Abstained = true, Confidence = 0, Year = null };
            }
            if (!result.Abstained)
            {
                if (result.Title != selected.Title || result.Year != selected.Year)
                { rejected.Add("identity"); reasons.Add("CandidateIdentityCanonicalized"); }
                // 候选身份固定；候选年份还要通过下方原始证据校验，不能自行增加排名权重。
                result = result with { Title = selected.Title, MediaType = selected.MediaType, Year = result.Year == selected.Year ? result.Year : null, SearchAliases = null };
            }
        }
        else if (!result.Abstained && result.SelectedCandidateId is int selectedId && locked is null &&
            !(context.Candidates ?? []).Take(5).Any(c => c.TmdbId == selectedId && c.MediaType == result.MediaType))
            throw new AiProviderLogicalException("AI 返回候选不在请求短表内");
        if (locked is not null)
        {
            if (result.Title != locked.Title || result.MediaType != locked.MediaType || result.Year != locked.Year ||
                result.SelectedCandidateId is int id && id != locked.TmdbId)
            { rejected.Add("identity"); reasons.Add("LockedIdentityChanged"); }
            result = result with { Title = locked.Title, MediaType = locked.MediaType, Year = locked.Year,
                SelectedCandidateId = locked.TmdbId, SearchAliases = null, RequiresIdentityVerification = false };
        }
        IEnumerable<string> rangeSources = Regex.IsMatch(request.FileName, @"(?i)(?:S\d{1,2})?E\d+|第\s*\d+")
            ? [request.FileName]
            : new[] { request.FileName }.Concat(request.RelativeSegments ?? (request.ParentFolderName is { } parent ? [parent] : []));
        bool unsafeRange = rangeSources.Any(HasUnsafeEpisodeRange);
        bool preserveConflictEvidence = result.Abstained
            && result.Validation?.ReasonCodes.Contains("EpisodicFieldsTypeConflict") == true;
        int? Guard(string field, int? model, int? known, int max)
        {
            if (known.HasValue) { if (model != known) { rejected.Add(field); reasons.Add("KnownFieldChanged"); } return known; }
            bool requested = context.TaskType != AiParseTaskType.FillMissingFields || (context.MissingFields ?? []).Contains(field);
            bool blocked = (context.RuleProvenance ?? []).Any(e => e.Field == field && e.Rejected)
                || unsafeRange && field is "episode" or "episodeEnd";
            if (model is null) return null;
            if (result.Abstained && !preserveConflictEvidence || !requested || blocked || model < 0 || model > max || !HasExplicitEvidence(field, model.Value, request))
            { rejected.Add(field); reasons.Add(blocked ? "RejectedSourceEvidence" : !requested ? "FieldNotRequested" : "UnsupportedField"); return null; }
            accepted.Add(field); return model;
        }
        int? season = Guard("season", result.Season, locked?.Season ?? request.RuleHintSeason, 99);
        int? episode = Guard("episode", result.Episode, locked?.Episode ?? request.RuleHintEpisode, 9999);
        int? end = Guard("episodeEnd", result.EpisodeEnd, locked?.EpisodeEnd ?? request.RuleHintEpisodeEnd, 9999);
        if (end.HasValue && (!episode.HasValue || end < episode || (request.RuleHintEpisode.HasValue && !request.RuleHintEpisodeEnd.HasValue)))
        { end = null; rejected.Add("episodeEnd"); reasons.Add("InvalidEpisodeRange"); }
        // 类型冲突必须先拒绝，不能用电影清空逻辑抹掉原有季集证据。
        bool episodicTypeConflict = result.MediaType == "movie" && HasEpisodicEvidence(request);
        if (episodicTypeConflict)
        {
            rejected.Add("type"); reasons.Add("EpisodicFieldsTypeConflict");
            result = result with { Abstained = true, Confidence = 0,
                SelectedCandidateId = locked?.TmdbId };
        }
        else if (result.MediaType == "movie") { season = null; episode = null; end = null; }
        if (locked is null)
        {
            string[] yearSources = [request.FileName, request.ParentFolderName ?? "", .. request.RelativeSegments ?? []];
            int? knownYear = request.RuleHintYear is int hint && MediaYearEvidence.ContainsYear(yearSources, hint) ? hint : null;
            if (knownYear.HasValue) result = result with { Year = knownYear };
            else if (result.Year is int year && !MediaYearEvidence.ContainsYear(yearSources, year))
            { result = result with { Year = null }; rejected.Add("year"); reasons.Add("UnsupportedField"); }
            if (request.RuleHintYear.HasValue && !knownYear.HasValue)
            { rejected.Add("year"); reasons.Add("UnsupportedRuleYear"); }
        }
        if (result.Abstained) reasons.Add("UnknownEvidence");
        if (context.SchemaVersion == 2 && !result.Abstained && locked is null)
        {
            bool candidateType = result.SelectedCandidateId is int id && (context.Candidates ?? []).Take(5)
                .Any(candidate => candidate.TmdbId == id && candidate.MediaType == result.MediaType);
            bool supported = candidateType || result.MediaType == "tv" && (HasEpisodicEvidence(request) || MediaTypeEvidence.HasTvSupport(request))
                || result.MediaType == "movie" && MediaTypeEvidence.HasMovieSupport(request);
            if (!supported)
            {
                if (result.MediaType is "movie" or "tv") rejected.Add("type");
                reasons.Add("TypeEvidenceMissing");
                bool titleSupported = HasSourceTitle(result.Title, request);
                if (!titleSupported) { rejected.Add("title"); reasons.Add("UnsupportedSearchTitle"); }
                result = result with { MediaType = "unknown", RequiresIdentityVerification = context.TaskType == AiParseTaskType.IdentifyWork
                    && titleSupported && context.RuleConflicts is not { Count: > 0 } && result.Details?.Conflicts is not { Count: > 0 } };
            }
            else result = result with { RequiresIdentityVerification = false };
        }
        return result with { Season = season, Episode = episode, EpisodeEnd = end,
            Validation = new(accepted, rejected.Distinct().ToArray(), reasons.Distinct().ToArray(), result.Validation?.OutputFields,
                result.Validation?.SchemaIssues) };
    }

    /// <summary>候选排名不能分辨同名作品，只有独立原始年份能排除同名候选</summary>
    private static bool HasUnresolvedNamesake(AiCandidateEvidence selected, AiParseRequest request)
    {
        string[] sources = [request.FileName, .. request.RelativeSegments ??
            (request.ParentFolderName is { } parent ? [parent] : [])];
        return MediaIdentityEvidence.HasUnresolvedNamesake(selected, (request.Context!.Candidates ?? []).Take(5), sources);
    }

    private static bool HasSourceTitle(string title, AiParseRequest request)
    {
        string Key(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        string key = Key(title);
        return key.Length >= 2 && new[] { request.FileName }.Concat(request.RelativeSegments ??
            (request.ParentFolderName is { } parent ? [parent] : [])).Any(source => Key(source).Contains(key, StringComparison.Ordinal));
    }

    private static bool HasUnsafeEpisodeRange(string source)
    {
        // 范围的任一端含小数、逆序或越界时整组拒绝，不能降格为单集。
        MatchCollection ranges = Regex.Matches(source,
            @"(?ix)(?: (?<![\p{L}0-9]) (?:S[0-9]{1,2})? E | 第\s* )
              (?<start>[0-9]+(?:\.[0-9]+)?) \s*[-~～—–]\s* E?(?<end>[0-9]+(?:\.[0-9]+)?)");
        return ranges.Any(range => !int.TryParse(range.Groups["start"].Value, out int start)
            || !int.TryParse(range.Groups["end"].Value, out int end) || start > 9999 || end > 9999 || end < start);
    }

    private static bool HasExplicitEvidence(string field, int value, AiParseRequest request)
    {
        // 规则来源只接受应用给出的字段；拒绝来源在调用方优先阻断。
        if ((request.Context!.RuleProvenance ?? []).Any(e => e.Field == field && e.Value == value && !e.Rejected)) return true;
        int[] numbers = ExplicitNumbers(field, request);
        return numbers.Length == 1 && numbers[0] == value;
    }

    private static bool HasEpisodicEvidence(AiParseRequest request)
    {
        bool InRange(string field, int? value) => value is >= 0 && value <= (field == "season" ? 99 : 9999);
        if (InRange("season", request.RuleHintSeason) || InRange("episode", request.RuleHintEpisode)
            || InRange("episodeEnd", request.RuleHintEpisodeEnd)) return true;
        AiLockedBinding? locked = request.Context!.LockedBinding;
        if (InRange("season", locked?.Season) || InRange("episode", locked?.Episode)
            || InRange("episodeEnd", locked?.EpisodeEnd)) return true;
        if ((request.Context!.RuleProvenance ?? []).Any(e => !e.Rejected
            && e.Field is "season" or "episode" or "episodeEnd" && InRange(e.Field, e.Value))) return true;
        // 无规则提示的独立解析也须保护明确 S/E/第几季集；裸续作数字不构成此证据。
        return new[] { "season", "episode" }.Any(field =>
            ExplicitNumbers(field, request).Any(value => InRange(field, value)));
    }

    private static int[] ExplicitNumbers(string field, AiParseRequest request)
    {
        string[] sources = [request.FileName, .. request.RelativeSegments ?? (request.ParentFolderName is { } parent ? [parent] : [])];
        string pattern = field switch
        {
            "season" => @"(?i)(?<![\p{L}\d])S(?<n>\d{1,2})(?=E\d|[^\p{L}\d]|$)|\bSeason\s*(?<n>\d{1,2})(?!\d)|\b(?<n>\d{1,2})(?:st|nd|rd|th)\s+Season\b|第\s*(?<n>\d{1,2})\s*季",
            "episode" => @"(?i)(?<![\p{L}\d])(?:S\d{1,2})?E(?<n>\d{1,4})(?![\p{L}\d]|[.]\d)(?:\s*[-~]\s*E?\d{1,4})?|第\s*(?<n>\d{1,4})\s*(?:集|话|話)",
            "episodeEnd" => @"(?i)(?<![\p{L}\d])(?:S\d{1,2})?E\d{1,4}\s*[-~]\s*E?(?<n>\d{1,4})(?![\p{L}\d]|[.]\d)",
            _ => "(?!)"
        };
        // 文件的明确单集边界优先于父目录整季范围；不把父目录范围扩到文件。
        if (field is "episode" or "episodeEnd" && Regex.IsMatch(request.FileName,
            @"(?i)(?<![\p{L}\d])(?:S\d{1,2})?E\d{1,4}(?![\p{L}\d]|[.]\d)|第\s*\d+\s*(?:集|话|話)"))
            sources = [request.FileName];
        return sources.SelectMany(s => Regex.Matches(s, pattern).Cast<Match>()).Select(m => int.TryParse(m.Groups["n"].Value, out int n) && ValidOrdinal(m.Value, n) ? n : -1)
            .Concat(field == "season" ? sources.SelectMany(s => Regex.Matches(s, @"第\s*(?<cn>[零〇一二两三四五六七八九十]{1,3})\s*季").Cast<Match>())
                .Select(m => ChineseSeason(m.Groups["cn"].Value)) : []).Distinct().ToArray();
    }
    private static bool ValidOrdinal(string marker, int number)
    {
        Match match = Regex.Match(marker, @"(?i)[0-9]+(?<suffix>st|nd|rd|th)\s+Season");
        if (!match.Success) return true;
        string expected = number % 100 is 11 or 12 or 13 ? "th" : (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return match.Groups["suffix"].Value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int ChineseSeason(string text)
    {
        int Digit(char c) => c switch { '零' or '〇' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4, '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1 };
        if (text.Length == 1) return text[0] == '十' ? 10 : Digit(text[0]);
        int ten = text.IndexOf('十');
        if (ten < 0 || ten > 1 || text.Length > ten + 2) return -1;
        int prefix = ten == 0 ? 1 : Digit(text[0]);
        int suffix = ten == text.Length - 1 ? 0 : Digit(text[^1]);
        return prefix > 0 && suffix >= 0 ? prefix * 10 + suffix : -1;
    }

}
