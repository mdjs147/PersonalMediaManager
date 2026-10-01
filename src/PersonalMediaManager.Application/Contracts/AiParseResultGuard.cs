using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>独立于供应商的任务结果安全守护</summary>
public static class AiParseResultGuard
{
    /// <summary>保护已有字段并拒绝无依据的模型补全</summary>
    public static AiParseResult Validate(AiParseResult result, AiParseRequest request)
    {
        if (request.Context is not { } context) return result;
        List<string> rejected = [.. result.Validation?.RejectedFields ?? []];
        List<string> reasons = [.. result.Validation?.ReasonCodes ?? []];
        List<string> accepted = [];
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
            if (result.Title != selected.Title || result.Year != selected.Year)
            { rejected.Add("identity"); reasons.Add("CandidateIdentityCanonicalized"); }
            // 候选身份固定；候选年份还要通过下方原始证据校验，不能自行增加排名权重。
            result = result with { Title = selected.Title, MediaType = selected.MediaType, Year = result.Year == selected.Year ? result.Year : null, SearchAliases = null };
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
                SelectedCandidateId = locked.TmdbId, SearchAliases = null };
        }
        IEnumerable<string> rangeSources = Regex.IsMatch(request.FileName, @"(?i)(?:S\d{1,2})?E\d+|第\s*\d+")
            ? [request.FileName]
            : new[] { request.FileName }.Concat(request.RelativeSegments ?? (request.ParentFolderName is { } parent ? [parent] : []));
        bool unsafeRange = rangeSources.Any(HasUnsafeEpisodeRange);
        int? Guard(string field, int? model, int? known, int max)
        {
            if (known.HasValue) { if (model != known) { rejected.Add(field); reasons.Add("KnownFieldChanged"); } return known; }
            bool requested = context.TaskType != AiParseTaskType.FillMissingFields || (context.MissingFields ?? []).Contains(field);
            bool blocked = (context.RuleProvenance ?? []).Any(e => e.Field == field && e.Rejected)
                || unsafeRange && field is "episode" or "episodeEnd";
            if (model is null) return null;
            if (result.Abstained || !requested || blocked || model < 0 || model > max || !HasExplicitEvidence(field, model.Value, request))
            { rejected.Add(field); reasons.Add(blocked ? "RejectedSourceEvidence" : !requested ? "FieldNotRequested" : "UnsupportedField"); return null; }
            accepted.Add(field); return model;
        }
        int? season = Guard("season", result.Season, locked?.Season ?? request.RuleHintSeason, 99);
        int? episode = Guard("episode", result.Episode, locked?.Episode ?? request.RuleHintEpisode, 9999);
        int? end = Guard("episodeEnd", result.EpisodeEnd, locked?.EpisodeEnd ?? request.RuleHintEpisodeEnd, 9999);
        if (end.HasValue && (!episode.HasValue || end < episode || (request.RuleHintEpisode.HasValue && !request.RuleHintEpisodeEnd.HasValue)))
        { end = null; rejected.Add("episodeEnd"); reasons.Add("InvalidEpisodeRange"); }
        if (result.MediaType == "movie") { season = null; episode = null; end = null; }
        if (locked is null)
        {
            if (request.RuleHintYear is int knownYear) result = result with { Year = knownYear };
            else if (result.Year is int year && !new[] { request.FileName, request.ParentFolderName ?? "" }.Concat(request.RelativeSegments ?? [])
                .Any(text => Regex.IsMatch(text, $@"(?<![\dA-Za-z]){year}(?![\dA-Za-z])")))
            { result = result with { Year = null }; rejected.Add("year"); reasons.Add("UnsupportedField"); }
        }
        if (result.Abstained) reasons.Add("UnknownEvidence");
        return result with { Season = season, Episode = episode, EpisodeEnd = end,
            Validation = new(accepted, rejected.Distinct().ToArray(), reasons.Distinct().ToArray(), result.Validation?.OutputFields) };
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
        string[] sources = [request.FileName, .. request.RelativeSegments ?? (request.ParentFolderName is { } parent ? [parent] : [])];
        string pattern = field switch
        {
            "season" => @"(?i)(?<![\p{L}\d])S(?<n>\d{1,2})(?=E\d|[^\p{L}\d]|$)|\bSeason\s*(?<n>\d{1,2})(?!\d)|\b(?<n>\d{1,2})(?:st|nd|rd|th)\s+Season\b|第\s*(?<n>\d{1,2})\s*季",
            "episode" => @"(?i)(?<![\p{L}\d])(?:S\d{1,2})?E(?<n>\d{1,4})(?!\d|[.]\d)(?:\s*[-~]\s*E?\d{1,4})?|第\s*(?<n>\d{1,4})\s*(?:集|话|話)",
            "episodeEnd" => @"(?i)E\d{1,4}\s*[-~]\s*E?(?<n>\d{1,4})(?!\d|[.]\d)",
            _ => "(?!)"
        };
        // 文件的明确单集边界优先于父目录整季范围；不把父目录范围扩到文件。
        if (field is "episode" or "episodeEnd" && Regex.IsMatch(request.FileName, @"(?i)(?:S\d{1,2})?E\d+|第\s*\d+\s*(?:集|话|話)"))
            sources = [request.FileName];
        int[] numbers = sources.SelectMany(s => Regex.Matches(s, pattern).Cast<Match>()).Select(m => int.TryParse(m.Groups["n"].Value, out int n) && ValidOrdinal(m.Value, n) ? n : -1)
            .Concat(field == "season" ? sources.SelectMany(s => Regex.Matches(s, @"第\s*(?<cn>[零〇一二两三四五六七八九十]{1,3})\s*季").Cast<Match>())
                .Select(m => ChineseSeason(m.Groups["cn"].Value)) : []).Distinct().ToArray();
        return numbers.Length == 1 && numbers[0] == value;
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
