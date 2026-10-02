using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    private const string StructuredNumber = @"[0-9]{1,4}(?:\.[0-9]{1,2})?(?:v[0-9]{1,3})?";
    private const string StructuredEpisodeSequence = @"(?<episodeToken>(?:Episode|EP?)[ ._-]*" + StructuredNumber
        + @"(?:(?:[ ]*[-~〜–][ ]*(?:Episode|EP?)?[ ]*|(?:Episode|EP?)[ ]*)" + StructuredNumber + @")*)(?![A-Za-z0-9])";
    private const string StructuredCjkNumber = @"[0-9零〇一二三四五六七八九十百千两兩壹贰貳弐叁參参肆伍陆陸柒捌玖拾佰仟]+";
    private static readonly Regex StructuredCombined = new(
        @"(?<![A-Za-z0-9])(?<![0-9](?:st|nd|rd|th)[ ._-]+)(?<seasonToken>(?:Season|SE?)[ ._-]*(?<season>[0-9]{1,2}))"
        + @"(?:[ ._-]*|[\]】][ ._-]*[\[【])" + StructuredEpisodeSequence, BaseOptions, RegexTimeout);
    private static readonly Regex StructuredNx = new(
        @"(?<![A-Za-z0-9])(?<season>[0-9]{1,2})x(?<episode>[0-9]{1,3})(?<fraction>\.[0-9]{1,2})?(?<revision>v[0-9]{1,3})?(?:[-~](?<end>[0-9]{1,3}))?(?![A-Za-z0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredSeason = new(
        @"(?<![A-Za-z0-9])(?<![0-9](?:st|nd|rd|th)[ ._-]+)(?:Season|SE?)[ ._-]*(?<season>[0-9]{1,2})(?![A-Za-z0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredEpisode = new(@"(?<![A-Za-z0-9])" + StructuredEpisodeSequence, BaseOptions, RegexTimeout);
    private static readonly Regex StructuredSeparatedLetter = new(@"^E[ ._-]+[0-9]", BaseOptions, RegexTimeout);
    private const string SeparatedEpisodeNeedsSeason = "分隔单字母E缺少独立季号，可能是标题缩写";
    private const string SeparatedEpisodeInsideAbbreviation = "分隔单字母E属于相邻字母缩写或年份语境，不能据季号补成集号";
    private static readonly Regex StructuredLetterPrefix = new(@"(?<![\p{L}\p{N}])[A-Za-z][ ._]+$", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredSeparatedYear = new(@"^E[ ._-]+(?:19|20)[0-9]{2}(?![0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredSequenceNumbers = new(
        @"(?<number>[0-9]{1,4})(?<fraction>\.[0-9]{1,2})?(?<revision>v[0-9]{1,3})?", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredCjk = new(@"第(?<number>" + StructuredCjkNumber
        + @")(?:(?<range>[-~〜–])(?<end>" + StructuredCjkNumber + @"))?(?<unit>季|集|话|話|期)", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredBracket = new(
        @"[\[【](?<episode>[0-9]{1,4})(?<fraction>\.[0-9]{1,2})?(?<revision>v[0-9]{1,3})?(?:[-~〜–](?<end>[0-9]{1,4}))?[\]】]", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredBareFraction = new(
        @"(?:^|[\s\[【._-])(?<number>[0-9]{1,3}\.[0-9])(?<revision>v[0-9]{1,3})?(?=$|[\s\]】]|\.(?:mkv|mp4|avi|ts|m2ts|mov|webm)$|[._-](?:1080|2160|720)[pP])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredNumericFile = new(
        @"^(?<episode>[0-9]{1,4})(?=$|\.(?:mkv|mp4|avi|ts|m2ts|mov|webm)$|[ ._-]+(?:480|720|1080|1440|2160)p(?![A-Za-z0-9]))", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredDate = new(
        @"(?<![A-Za-z0-9])(?<date>(?:19|20)[0-9]{2}(?:[-._]?[0-9]{2}){2}|[0-9]{6})(?![A-Za-z0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredPart = new(
        @"(?<![A-Za-z0-9])(?<kind>Volume|Vol|Disc|Disk|CD|Part|Cour)[ ._-]*(?<number>[0-9]{1,3})(?![A-Za-z0-9])|(?<![A-Za-z0-9])(?<number>[1-9][0-9]?)(?:st|nd|rd|th)[ ._-]+(?<kind>Cour)(?![A-Za-z])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredAbsolute = new(
        @"(?<![A-Za-z0-9])(?:#|No\.?)[ ._-]*(?<number>[0-9]{1,5})(?![A-Za-z0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredContent = new(
        @"(?<![A-Za-z])(?<kind>OVA|OAD|SP|NCOP|NCED|PV|Trailer|Movie|TV)(?:[ ._-]*(?<number>[0-9]{1,3}))?(?![A-Za-z0-9])|(?<kind>特别篇|特別篇|番外|特典|剧场版|劇場版|映画)(?:[ ._-]*(?<number>[0-9]{1,3}))?", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredDimensions = new(
        @"(?<![A-Za-z0-9])(?:[0-9]{3,4}\s*[x×]\s*[0-9]{3,4}|[xh]\.?26[45]|AV1|Main10p)(?![A-Za-z0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredAudioAnchor = new(
        @"(?<![A-Za-z0-9])(?:AAC|AC3|EAC3|DDP?|DTS(?:[ .-]?HD)?|TrueHD|FLAC|Opus|MP3)(?![A-Za-z0-9])", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredAudioChannels = new(@"^[1-9]\.[0-2]$", BaseOptions, RegexTimeout);
    private static readonly Regex StructuredPackageRange = new(@"[0-9]{1,4}\s*[-~〜–]\s*[0-9]{1,4}|[全共][0-9]{1,4}[集話话期]|全集", BaseOptions, RegexTimeout);

    /// <summary>两条解析路径共用的显式证据裁决与最终安全门</summary>
    private static RuleParseResult ApplyStructuredEvidence(RuleParseResult result, FileParseContext context)
    {
        List<RuleNumberingEvidence> items = [.. result.NumberingEvidence ?? []];
        CollectStructuredLayer(items, context.FileName, null);
        for (int i = context.RelativeSegments.Count - 1; i >= 0; i--)
            CollectStructuredLayer(items, context.RelativeSegments[i], i);

        bool independentSeason = items.Any(e => e.Kind == RuleNumberingKind.Season && e.State == RuleEvidenceState.Accepted);
        for (int i = 0; i < items.Count; i++)
        {
            RuleNumberingEvidence item = items[i];
            if (independentSeason && item.Reason == SeparatedEpisodeNeedsSeason)
                items[i] = item with { State = RuleEvidenceState.Accepted, Reason = "独立显式季号支持分隔集标记" };
            if (IsMixedPackageParentContent(item, items, context))
                items[i] = item with { Reason = "混合整包父目录的内容种类只作包范围候选，不传播到当前文件" };
        }

        RuleNumberingEvidence[] userNamespaces = items.Where(e => e.RuleKey?.StartsWith("user:", StringComparison.Ordinal) == true).ToArray();
        bool suppressedLocalNumber = false;
        for (int i = 0; i < items.Count; i++)
        {
            RuleNumberingEvidence item = items[i];
            bool weakNumber = item.Field == "episode" && item.State == RuleEvidenceState.Accepted
                && (item.Token.All(char.IsAsciiDigit) || item.Token.StartsWith('[') || item.Token.StartsWith('【'));
            if (!weakNumber) continue;
            bool blocked = userNamespaces.Any(candidate => candidate.Source is "RelativePath" or "FullPath"
                || candidate.Source == item.Source && candidate.SegmentIndex == item.SegmentIndex
                    && candidate.Start < item.Start + item.Length && candidate.Start + candidate.Length > item.Start);
            if (!blocked) continue;
            suppressedLocalNumber = true;
            items[i] = item with { State = RuleEvidenceState.Candidate, Reason = "该原文区间已有用户声明的来源编号空间，禁止裸号启发式覆盖" };
        }

        List<string> conflicts = [.. result.Conflicts ?? []];
        HashSet<string> rejected = new(result.RejectedFields ?? [], StringComparer.OrdinalIgnoreCase);
        List<RuleFieldEvidence> fields = [.. result.FieldEvidence ?? []];
        foreach (RuleNumberingEvidence issue in items.Where(e => e.Kind == RuleNumberingKind.Issue && e.Source == "FileName").ToArray())
            if (fields.Any(f => f.Field == "episode" && f.Source == "UserRule" && f.Value == issue.Value))
                items.Add(issue with { Kind = RuleNumberingKind.LocalEpisode, State = RuleEvidenceState.Accepted,
                    Field = "episode", Reason = "用户规则明确声明该独立期号为当前集", RuleKey = $"user:{result.MatchedRuleId}:capture:episode" });
        bool forcedMovie = result.ForceType && result.MediaType == "movie";
        bool mappedSeason = result.NamingEvidence?.SeasonMappingSource is "LicensedSeasonCatalogue" or "ConfirmedSeriesNumbering";
        int? season = result.Season;
        int? episode = result.Episode;
        int? end = result.EpisodeEnd;
        List<RuleNumberingEvidence> seasons = items.Where(e => e.Field == "season" && e.State == RuleEvidenceState.Accepted).ToList();
        List<RuleNumberingEvidence> episodes = items.Where(e => e.Field == "episode" && e.State == RuleEvidenceState.Accepted).ToList();
        List<RuleNumberingEvidence> fileEpisodes = episodes.Where(e => e.SegmentIndex is null).ToList();
        bool invalidSeason = items.Any(e => e.Field == "season" && e.State == RuleEvidenceState.Rejected);
        bool fileEpisodeRejected = items.Any(e => e.SegmentIndex is null && e.Field == "episode"
            && e.State == RuleEvidenceState.Rejected);
        bool unresolvedSeparatedEpisode = items.Any(e => e.Source == "FileName"
            && e.Reason is SeparatedEpisodeNeedsSeason or SeparatedEpisodeInsideAbbreviation);
        bool nearestEpisodeRejected = items.FirstOrDefault(e => e.Field == "episode"
            && e.State is RuleEvidenceState.Accepted or RuleEvidenceState.Rejected)?.State == RuleEvidenceState.Rejected;
        bool unresolvedFileNumber = items.Any(e => e.SegmentIndex is null && e.Kind is RuleNumberingKind.AirDate
            or RuleNumberingKind.ShortAirDate or RuleNumberingKind.Volume or RuleNumberingKind.Disc
            or RuleNumberingKind.Part or RuleNumberingKind.Cour or RuleNumberingKind.Absolute);
        // “期”可指综艺期号或放送分期；只沿用用户规则明确的同值集号解释，不从词面补季。
        unresolvedFileNumber |= items.Any(item => item.SegmentIndex is null && item.Kind == RuleNumberingKind.Issue
            && !fields.Any(f => f.Field == "episode" && f.Source == "UserRule" && f.Value == item.Value));
        List<RuleNumberingEvidence> applicableContent = items.Where(e => e.Kind == RuleNumberingKind.ContentKind
            && !IsMixedPackageParentContent(e, items, context)).ToList();
        bool unresolvedContentNumber = applicableContent.Any(e => e.SegmentIndex is null
            && e.Kind == RuleNumberingKind.ContentKind && e.Value is not null
            && (IsSpecialContent(e.TextValue) || IsMovieContent(e.TextValue)));
        bool special = applicableContent.Any(e => IsSpecialContent(e.TextValue));
        bool explicitSpecials = seasons.Any(e => e.Value == 0);

        if (!forcedMovie)
        {
            bool unmappedSeason = items.Any(e => IsNonLocalCapturedSeason(e, season));
            if ((invalidSeason || unmappedSeason) && seasons.Count == 0 && !mappedSeason)
            {
                season = null;
                rejected.Add("season");
            }
            if (!rejected.Contains("season"))
            {
                RuleNumberingEvidence? preferredSeason = seasons.FirstOrDefault();
                bool userSeason = fields.Any(e => e.Field == "season" && e.Source == "UserRule")
                    && !items.Any(e => e.SegmentIndex is null && e.Kind is RuleNumberingKind.Cour or RuleNumberingKind.Issue && e.Value == season);
                if (season is null || !userSeason && !mappedSeason && preferredSeason?.SegmentIndex is null)
                    season = preferredSeason?.Value ?? season;
            }
            if (fileEpisodeRejected || nearestEpisodeRejected || rejected.Contains("episode")
                || (unresolvedFileNumber && fileEpisodes.Count == 0)
                || (unresolvedSeparatedEpisode && episode is not null && fileEpisodes.Count == 0)
                || (suppressedLocalNumber && episodes.Count == 0)
                || ((special || unresolvedContentNumber) && fileEpisodes.Count == 0))
            {
                episode = null;
                end = null;
                rejected.Add("episode");
                rejected.Add("episodeEnd");
            }
            else
            {
                RuleNumberingEvidence? preferred = fileEpisodes.FirstOrDefault() ?? episodes.FirstOrDefault();
                if (preferred is not null)
                {
                    // 已显式捕获的用户值保留优先；其余旧提取结果由文件的完整语法纠正。
                    bool userEpisode = fields.Any(e => e.Field == "episode" && e.Source == "UserRule")
                        && !items.Any(e => e.SegmentIndex is null && IsNonLocalCapturedEpisode(e, episode));
                    if (episode is null || !userEpisode && fileEpisodes.Count > 0) episode = preferred.Value;
                    if (episode == preferred.Value) end = preferred.End;
                }
            }
            if (special && !explicitSpecials && season == 0 && !mappedSeason)
            {
                season = null;
                rejected.Add("season");
            }
        }

        AddStructuredConflicts("season", season, seasons, conflicts);
        // 目录范围表示整包，不和当前文件单集进行错误冲突比较。
        AddStructuredConflicts("episode", episode, episodes, conflicts);
        if (fileEpisodes.Where(e => e.Value == episode).Select(e => e.End).Distinct().Count() > 1)
            conflicts.Add("episodeEnd：同文件显式集号的范围终点不一致，保留优先范围等待核验");
        bool movieHint = applicableContent.Any(e => IsMovieContent(e.TextValue));
        if (!forcedMovie && movieHint && (seasons.Count > 0 || episodes.Count > 0))
            conflicts.Add("mediaType：电影内容标记与显式季集标记冲突");
        if (!forcedMovie && special && seasons.Any(e => e.Value > 0))
            conflicts.Add("mediaType：特别篇内容标记与正片季号并存，需要目录核验");

        bool completedCoordinate = season != result.Season || episode != result.Episode;
        string mediaType = !forcedMovie && completedCoordinate && (season is not null || episode is not null)
            ? "tv" : result.MediaType;
        if (!result.ForceType && special && !explicitSpecials) mediaType = "unknown";
        if (!result.ForceType && movieHint && seasons.Count == 0 && episodes.Count == 0) mediaType = "movie";
        if (result.ForceType) mediaType = result.MediaType;
        double confidence = completedCoordinate
            ? Math.Max(result.Confidence, ScoreConfidence(result.Title, mediaType, season, episode, result.Year)) : result.Confidence;
        if (rejected.Count > 0 || conflicts.Count > 0 || special && !explicitSpecials
            || unresolvedFileNumber && fileEpisodes.Count == 0)
            confidence = Math.Min(confidence, 0.49);
        if (!result.HasIdentityEvidence) confidence = Math.Min(confidence, result.Confidence);

        // 非季内编号保留原始区间与被拒绝的旧值，不再作为已采纳字段发送给后续 AI。
        foreach (RuleFieldEvidence field in fields.ToArray())
        {
            bool invalidCoordinate = !forcedMovie
                && (field.Field == "episode" && field.Value != episode || field.Field == "season" && field.Value != season);
            bool invalidYear = field.Field == "year" && field.Value != result.Year;
            if (!invalidCoordinate && !invalidYear) continue;
            RuleNumberingEvidence? source = items.FirstOrDefault(item => invalidYear
                ? item.Kind == RuleNumberingKind.AirDate && item.TextValue?.StartsWith(field.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) == true
                : field.Field == "season" ? IsNonLocalCapturedSeason(item, field.Value) : IsNonLocalCapturedEpisode(item, field.Value));
            if (source is null) continue;
            items.Add(source with { State = RuleEvidenceState.Rejected, Field = field.Field, Value = field.Value,
                Reason = $"旧捕获 {field.Field}={field.Value} 来自 {source.Kind}，不能作为已采纳季集或作品年份" });
            fields.Remove(field);
        }

        for (int i = 0; i < items.Count; i++)
        {
            RuleNumberingEvidence item = items[i];
            if (item.State != RuleEvidenceState.Accepted) continue;
            bool hasConflict = conflicts.Any(c => c.StartsWith(item.Field + "：", StringComparison.Ordinal)
                || item.Field == "episode" && c.StartsWith("episodeEnd：", StringComparison.Ordinal));
            bool blocked = rejected.Contains(item.Field) || forcedMovie;
            if (hasConflict || blocked) items[i] = item with
            {
                State = hasConflict ? RuleEvidenceState.Conflict : RuleEvidenceState.Candidate,
                Reason = hasConflict ? "显式证据冲突，保留优先值等待核验" : "字段被拒绝或强制类型阻止补入",
            };
            else if (item.Value is int value && (item.Field == "season" && value == season || item.Field == "episode" && value == episode))
                fields.Add(new(item.Field, value, item.SegmentIndex is int segment ? $"RelativeSegment:{segment}" : "FileName", item.Token));
        }
        return result with
        {
            Season = season, Episode = episode, EpisodeEnd = end, MediaType = mediaType,
            Confidence = confidence, NumberingEvidence = items,
            FieldEvidence = fields.Distinct().ToArray(),
            Conflicts = conflicts.Count == 0 ? null : conflicts.Distinct().ToArray(),
            RejectedFields = rejected.Count == 0 ? null : rejected.ToArray(),
        };
    }

    private static void AddStructuredConflicts(string field, int? selected, List<RuleNumberingEvidence> evidence, List<string> conflicts)
    {
        int? first = selected ?? evidence.FirstOrDefault()?.Value;
        if (first is null) return;
        foreach (RuleNumberingEvidence other in evidence.Where(e => e.Value != first))
            conflicts.Add($"{field}：优先值 {first} 与 {(other.SegmentIndex is int i ? $"目录[{i}]" : "文件")} 显式标记 {other.Token} 冲突");
    }

    private static bool IsSpecialContent(string? kind) => kind?.ToUpperInvariant()
        is "OVA" or "OAD" or "SP" or "NCOP" or "NCED" or "PV" or "特别篇" or "特別篇" or "番外" or "特典";
    // Movie / Trailer / TV 同时可能是普通标题词，只保留候选，不能单凭词面改类型或删续作数字。
    private static bool IsMovieContent(string? kind) => kind is "剧场版" or "劇場版" or "映画";

    private static bool IsNonLocalCapturedEpisode(RuleNumberingEvidence item, int? episode)
    {
        if (episode is null) return false;
        if (item.Kind is RuleNumberingKind.AirDate or RuleNumberingKind.ShortAirDate)
        {
            string digits = new(item.Token.Where(char.IsAsciiDigit).ToArray());
            return int.TryParse(digits, out int date) && date == episode
                || digits.Length >= 4 && int.TryParse(digits.AsSpan(digits.Length - 4), out int monthDay) && monthDay == episode;
        }
        return item.Value == episode && (item.Kind is RuleNumberingKind.Volume or RuleNumberingKind.Disc
            or RuleNumberingKind.Part or RuleNumberingKind.Cour or RuleNumberingKind.Absolute
            || item.Kind == RuleNumberingKind.ContentKind && (IsSpecialContent(item.TextValue) || IsMovieContent(item.TextValue)));
    }

    private static bool IsNonLocalCapturedSeason(RuleNumberingEvidence item, int? season) =>
        IsNonLocalCapturedEpisode(item, season) || season is not null && item.Kind == RuleNumberingKind.Issue && item.Value == season;

    /// <summary>仅作一对一全角折叠，维持全部原文索引</summary>
    private static string FoldStructuredWidth(string value) => new(value.Select(c => c is >= '\uff01' and <= '\uff5e'
        ? (char)(c - 0xfee0) : c == '\u3000' ? ' ' : c).ToArray());

    /// <summary>孤立单字母 E 加分隔年份不足以构成集号，避免误拆 WALL-E 等片名</summary>
    private static bool IsTrustedStructuredEpisode(Match match, bool hasIndependentSeason = false, string? source = null) => match.Success
        && !IsStructuredAbbreviationEpisode(match, source)
        && (hasIndependentSeason || !SafeIsMatch(StructuredSeparatedLetter, match.Groups["episodeToken"].Value));

    private static bool IsStructuredAbbreviationEpisode(Match match, string? source) =>
        SafeIsMatch(StructuredSeparatedLetter, match.Groups["episodeToken"].Value)
        && (SafeIsMatch(StructuredSeparatedYear, match.Groups["episodeToken"].Value)
            || source is not null && SafeIsMatch(StructuredLetterPrefix, source[..match.Index]));

    /// <summary>标题与字段提取共用可信集号边界，继续跳过弱片段和未知括号</summary>
    private static Match FindStructuredEpisodeBoundary(string title, bool hasIndependentSeason = false)
    {
        foreach (Match match in SafeMatches(StructuredEpisode, title))
            if (IsTrustedStructuredEpisode(match, hasIndependentSeason, title) && !IntersectsTitleParentheses(title, match)) return match;
        return Match.Empty;
    }

    /// <summary>父目录同块明确混合TV与其他内容的包范围不能套到每个单文件</summary>
    private static bool IsMixedPackageParentContent(RuleNumberingEvidence item, List<RuleNumberingEvidence> items, FileParseContext context)
    {
        if (item.Kind != RuleNumberingKind.ContentKind || item.SegmentIndex is not int segment) return false;
        string source = context.RelativeSegments[segment];
        Match? block = SafeMatches(TitleMetadataBlock, source).FirstOrDefault(m => item.Start >= m.Index
            && item.Start + item.Length <= m.Index + m.Length);
        int start = block?.Index ?? 0, end = block is null ? source.Length : block.Index + block.Length;
        if (!SafeIsMatch(StructuredPackageRange, source[start..end])) return false;
        RuleNumberingEvidence[] kinds = items.Where(e => e.Kind == RuleNumberingKind.ContentKind
            && e.SegmentIndex == segment && e.Start >= start && e.Start + e.Length <= end).ToArray();
        return kinds.Any(e => string.Equals(e.TextValue, "TV", StringComparison.OrdinalIgnoreCase))
            && kinds.Any(e => IsSpecialContent(e.TextValue) || IsMovieContent(e.TextValue));
    }

    /// <summary>声道必须有邻近音频锚点及可验证技术区域，不能仅按小数值豁免</summary>
    private static bool IsStructuredAudioChannel(string text, int start, int length, IReadOnlyList<Match> audioAnchors)
    {
        if (!SafeIsMatch(StructuredAudioChannels, text.Substring(start, length))) return false;
        foreach (Match anchor in audioAnchors)
        {
            if (anchor.Index > start + length + 64 || anchor.Index + anchor.Length < start - 64) continue;
            int gapStart = Math.Min(start + length, anchor.Index + anchor.Length);
            int gapEnd = Math.Max(start, anchor.Index);
            if (gapEnd >= gapStart && text[gapStart..gapEnd].All(c => char.IsWhiteSpace(c)
                || c is '.' or '_' or '-' or '[' or ']' or '【' or '】' or '(' or ')' or '（' or '）')) return true;
            int first = Math.Min(start, anchor.Index), last = Math.Max(start + length, anchor.Index + anchor.Length);
            string region = new(text[first..last].Select(c => c is '[' or ']' or '【' or '】' or '(' or ')' or '（' or '）' ? ' ' : c).ToArray());
            if (TryCleanTechnicalRegion(region, allowReleaseSuffix: false, out _)) return true;
        }
        return false;
    }

    private static void CollectStructuredLayer(List<RuleNumberingEvidence> items, string raw, int? segment)
    {
        string text = FoldStructuredWidth(raw);
        IReadOnlyList<Match> audioAnchors = SafeMatches(StructuredAudioAnchor, text);
        List<(int Start, int End)> occupied = [];
        RuleNumberingEvidence Evidence(RuleNumberingKind kind, RuleEvidenceState state, string field,
            int start, int length, int? value = null, int? end = null, IReadOnlyList<int>? values = null,
            string? textValue = null, string? reason = null) => new(kind, state, field,
                segment is null ? "FileName" : "RelativeSegment", segment, start, length,
                raw.Substring(start, length), value, end, values, textValue, reason,
                RuleKey: $"builtin:structured:{kind}");
        bool Overlaps(int start, int length) => occupied.Any(o => start < o.End && start + length > o.Start);
        void AddEpisode(int start, int length, int value, int? end = null, RuleNumberingKind kind = RuleNumberingKind.LocalEpisode,
            IReadOnlyList<int>? values = null)
        {
            bool reversed = end < value;
            bool disjoint = values is not null && values.Zip(values.Skip(1)).Any(p => p.Second != p.First + 1);
            RuleEvidenceState state = reversed || disjoint ? RuleEvidenceState.Rejected
                : segment is not null && end is not null ? RuleEvidenceState.Candidate : RuleEvidenceState.Accepted;
            items.Add(Evidence(kind, state, "episode", start, length, value, end, values,
                reason: reversed ? "反向范围不能作为当前集" : disjoint ? "显式列表不连续，不能改成闭区间"
                    : state == RuleEvidenceState.Candidate ? "目录范围仅描述整包" : null));
        }
        void AddRevision(Group revision, int offset = 0)
        {
            if (revision.Success && int.TryParse(revision.Value.AsSpan(1), out int value))
                items.Add(Evidence(RuleNumberingKind.ReleaseRevision, RuleEvidenceState.Candidate, "revision",
                    offset + revision.Index, revision.Length, value));
        }
        void AddSequence(Group sequence)
        {
            Match[] numbers = SafeMatches(StructuredSequenceNumbers, sequence.Value).ToArray();
            if (numbers.Length == 0) return;
            foreach (Match n in numbers) AddRevision(n.Groups["revision"], sequence.Index);
            Match? fractional = numbers.FirstOrDefault(n => n.Groups["fraction"].Success);
            if (fractional is not null)
            {
                items.Add(Evidence(RuleNumberingKind.FractionalEpisode, RuleEvidenceState.Rejected, "episode",
                    sequence.Index, sequence.Length, textValue: fractional.Groups["number"].Value + fractional.Groups["fraction"].Value,
                    reason: "小数来源集号未经目录映射，禁止截成整数或从父目录回填"));
                return;
            }
            int[] values = numbers.Select(n => int.Parse(n.Groups["number"].Value, CultureInfo.InvariantCulture)).ToArray();
            bool range = sequence.Value[(numbers[0].Index + numbers[0].Length)..].IndexOfAny(['-', '~', '〜', '–']) >= 0;
            if (values.Length > 32 || range && values.Length > 2)
            {
                items.Add(Evidence(RuleNumberingKind.ExplicitList, RuleEvidenceState.Rejected, "episode",
                    sequence.Index, sequence.Length, values[0], values[^1], values,
                    reason: "过长列表或范围与枚举混用，需要逐项目录核验"));
                return;
            }
            AddEpisode(sequence.Index, sequence.Length, values[0], values.Length > 1 ? values[^1] : null,
                values.Length == 1 ? RuleNumberingKind.LocalEpisode : range ? RuleNumberingKind.InclusiveRange : RuleNumberingKind.ExplicitList,
                !range && values.Length > 1 ? values : null);
        }

        foreach (Match m in SafeMatches(StructuredCombined, text))
        {
            Group s = m.Groups["seasonToken"];
            items.Add(Evidence(RuleNumberingKind.Season, RuleEvidenceState.Accepted, "season", s.Index, s.Length,
                int.Parse(m.Groups["season"].Value, CultureInfo.InvariantCulture)));
            AddSequence(m.Groups["episodeToken"]);
            occupied.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in SafeMatches(StructuredNx, text))
        {
            if (Overlaps(m.Index, m.Length)) continue;
            items.Add(Evidence(RuleNumberingKind.Season, RuleEvidenceState.Accepted, "season", m.Index, m.Length,
                int.Parse(m.Groups["season"].Value, CultureInfo.InvariantCulture)));
            AddRevision(m.Groups["revision"]);
            if (m.Groups["fraction"].Success)
                items.Add(Evidence(RuleNumberingKind.FractionalEpisode, RuleEvidenceState.Rejected, "episode", m.Index, m.Length,
                    textValue: m.Groups["episode"].Value + m.Groups["fraction"].Value, reason: "小数来源集号不能截成整数"));
            else AddEpisode(m.Index, m.Length, int.Parse(m.Groups["episode"].Value, CultureInfo.InvariantCulture),
                TryParseInt(m.Groups["end"].Value), m.Groups["end"].Success ? RuleNumberingKind.InclusiveRange : RuleNumberingKind.LocalEpisode);
            occupied.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in SafeMatches(StructuredSeason, text))
        {
            if (Overlaps(m.Index, m.Length)) continue;
            items.Add(Evidence(RuleNumberingKind.Season, RuleEvidenceState.Accepted, "season", m.Index, m.Length,
                int.Parse(m.Groups["season"].Value, CultureInfo.InvariantCulture)));
        }
        foreach (Match m in SafeMatches(BuiltinRulesCatalog.SeasonOrdinalLatin, text))
            items.Add(Evidence(RuleNumberingKind.Season, RuleEvidenceState.Accepted, "season", m.Index, m.Length,
                int.Parse(m.Groups["season"].Value, CultureInfo.InvariantCulture)));
        foreach (Match m in SafeMatches(StructuredEpisode, text))
        {
            if (Overlaps(m.Index, m.Length)) continue;
            if (!IsTrustedStructuredEpisode(m, source: text))
            {
                int first = items.Count;
                AddSequence(m.Groups["episodeToken"]);
                for (int i = first; i < items.Count; i++)
                    if (items[i].Field == "episode" && items[i].State == RuleEvidenceState.Accepted)
                        items[i] = items[i] with { State = RuleEvidenceState.Candidate,
                            Reason = IsStructuredAbbreviationEpisode(m, text) ? SeparatedEpisodeInsideAbbreviation : SeparatedEpisodeNeedsSeason };
                continue;
            }
            AddSequence(m.Groups["episodeToken"]);
            occupied.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in SafeMatches(StructuredCjk, text))
        {
            string unit = m.Groups["unit"].Value;
            int? value = ParseStrictStructuredNumber(m.Groups["number"].Value);
            int? last = m.Groups["end"].Success ? ParseStrictStructuredNumber(m.Groups["end"].Value) : null;
            bool invalid = value is null || m.Groups["end"].Success && last is null || unit == "季" && value > 99;
            if (invalid)
                items.Add(Evidence(RuleNumberingKind.InvalidNumber, RuleEvidenceState.Rejected, unit == "季" ? "season" : "episode",
                    m.Index, m.Length, reason: "非法或越界中文数词，不能截取局部数字"));
            else if (unit == "期")
                items.Add(Evidence(RuleNumberingKind.Issue, RuleEvidenceState.Candidate, "issue", m.Index, m.Length, value, last,
                    reason: "期号可能表示节目期次或放送分期，不能从词面推断季号"));
            else if (unit == "季")
                items.Add(Evidence(RuleNumberingKind.Season, last is null ? RuleEvidenceState.Accepted : RuleEvidenceState.Candidate,
                    "season", m.Index, m.Length, value, last, reason: last is null ? null : "季范围不能作为当前季"));
            else AddEpisode(m.Index, m.Length, value!.Value, last, last is null ? RuleNumberingKind.LocalEpisode : RuleNumberingKind.InclusiveRange);
        }
        foreach (Match m in SafeMatches(StructuredBracket, text))
        {
            int value = int.Parse(m.Groups["episode"].Value, CultureInfo.InvariantCulture);
            if (value is >= 1900 and <= 2099) continue;
            AddRevision(m.Groups["revision"]);
            if (m.Groups["fraction"].Success)
            {
                Group n = m.Groups["episode"];
                int length = n.Length + m.Groups["fraction"].Length;
                bool audio = !m.Groups["revision"].Success && IsStructuredAudioChannel(text, n.Index, length, audioAnchors);
                items.Add(Evidence(audio ? RuleNumberingKind.TechnicalNumber : RuleNumberingKind.FractionalEpisode,
                    RuleEvidenceState.Rejected, audio ? "technical" : "episode", m.Index, m.Length,
                    textValue: n.Value + m.Groups["fraction"].Value,
                    reason: audio ? "音频技术上下文中的声道不能作为集号" : "小数来源集号不能截成整数"));
            }
            else AddEpisode(m.Index, m.Length, value, TryParseInt(m.Groups["end"].Value),
                m.Groups["end"].Success ? RuleNumberingKind.InclusiveRange : RuleNumberingKind.LocalEpisode);
        }
        foreach (Match m in SafeMatches(StructuredBareFraction, text))
        {
            Group n = m.Groups["number"];
            // 只豁免已由音频上下文证明的声道；显式 E/EP 小数始终走上面的拒绝分支。
            if (!m.Groups["revision"].Success && IsStructuredAudioChannel(text, n.Index, n.Length, audioAnchors)) continue;
            if (items.Any(e => e.SegmentIndex == segment && e.Kind == RuleNumberingKind.FractionalEpisode
                && e.Start <= n.Index && e.Start + e.Length >= n.Index + n.Length)) continue;
            items.Add(Evidence(RuleNumberingKind.FractionalEpisode, RuleEvidenceState.Rejected, "episode", n.Index, n.Length,
                textValue: n.Value, reason: "小数编号只能作为待核验候选"));
            AddRevision(m.Groups["revision"]);
        }
        if (segment is null)
        {
            foreach (Match m in SafeMatches(StructuredNumericFile, text))
            {
                int value = int.Parse(m.Groups["episode"].Value, CultureInfo.InvariantCulture);
                if (value is >= 1900 and <= 2099) continue;
                Group n = m.Groups["episode"];
                AddEpisode(n.Index, n.Length, value);
                AddRevision(m.Groups["revision"]);
            }
            string stem = Path.GetFileNameWithoutExtension(text);
            Match dash = SafeMatch(DashEpisode, stem);
            if (!dash.Success) dash = MatchDashEpisodeWithTechnicalTail(stem);
            if (dash.Success)
            {
                Group n = dash.Groups["episode"];
                AddEpisode(n.Index, n.Length, int.Parse(n.Value, CultureInfo.InvariantCulture));
                AddRevision(SafeMatch(StructuredSequenceNumbers, stem[n.Index..]).Groups["revision"], n.Index);
            }
        }
        foreach (Match m in SafeMatches(StructuredDate, text))
        {
            string compact = new(m.Value.Where(char.IsAsciiDigit).ToArray());
            bool shortDate = compact.Length == 6;
            bool valid = DateOnly.TryParseExact(shortDate ? "20" + compact : compact, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date);
            items.Add(Evidence(shortDate ? RuleNumberingKind.ShortAirDate : RuleNumberingKind.AirDate,
                valid ? RuleEvidenceState.Candidate : RuleEvidenceState.Rejected, "airDate", m.Index, m.Length,
                textValue: valid && !shortDate ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : m.Value,
                reason: !valid ? "无效日历日期" : shortDate ? "世纪未知，不能映射为季内集号" : "播出日期需要目录映射，不能作为作品年份或季内集号"));
        }
        foreach (Match m in SafeMatches(StructuredPart, text))
        {
            RuleNumberingKind kind = m.Groups["kind"].Value.ToLowerInvariant() switch
            {
                "vol" or "volume" => RuleNumberingKind.Volume,
                "disc" or "disk" or "cd" => RuleNumberingKind.Disc,
                "part" => RuleNumberingKind.Part,
                _ => RuleNumberingKind.Cour,
            };
            items.Add(Evidence(kind, RuleEvidenceState.Candidate, kind.ToString().ToLowerInvariant(), m.Index, m.Length,
                int.Parse(m.Groups["number"].Value, CultureInfo.InvariantCulture), reason: "卷盘分部编号不等于官方季集编号"));
        }
        foreach (Match m in SafeMatches(StructuredAbsolute, text))
            items.Add(Evidence(RuleNumberingKind.Absolute, RuleEvidenceState.Candidate, "absolute", m.Index, m.Length,
                int.Parse(m.Groups["number"].Value, CultureInfo.InvariantCulture), reason: "绝对编号需要目录映射为季内集号"));
        foreach (Match m in SafeMatches(StructuredContent, text))
            items.Add(Evidence(RuleNumberingKind.ContentKind, RuleEvidenceState.Candidate, "contentKind", m.Index, m.Length,
                TryParseInt(m.Groups["number"].Value), textValue: m.Groups["kind"].Value,
                reason: "内容种类不证明作品类型或 Season 0 映射"));
        foreach (Match m in SafeMatches(StructuredDimensions, text))
            items.Add(Evidence(RuleNumberingKind.TechnicalNumber, RuleEvidenceState.Rejected, "technical", m.Index, m.Length,
                reason: "技术参数不参与季集和年份"));
    }

    /// <summary>严格解析中文常用及财务数词，拒绝数字堆叠与乱序单位</summary>
    private static int? ParseStrictStructuredNumber(string text)
    {
        if (text.All(char.IsAsciiDigit))
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int arabic) && arabic <= 9999 ? arabic : null;
        string normalized = new(text.Select(c => c switch
        {
            '〇' => '零', '壹' => '一', '贰' or '貳' or '弐' or '两' or '兩' => '二',
            '叁' or '參' or '参' => '三', '肆' => '四', '伍' => '五', '陆' or '陸' => '六',
            '柒' => '七', '捌' => '八', '玖' => '九', '拾' => '十', '佰' => '百', '仟' => '千', _ => c,
        }).ToArray());
        if (normalized == "零") return 0;
        const string digits = "零一二三四五六七八九";
        int total = 0, digit = 0, previousUnit = 10000;
        foreach (char c in normalized)
        {
            int value = digits.IndexOf(c);
            if (value >= 0) { digit = value; continue; }
            int unit = c switch { '十' => 10, '百' => 100, '千' => 1000, _ => 0 };
            if (unit == 0 || unit >= previousUnit) return null;
            total += (digit == 0 ? 1 : digit) * unit;
            digit = 0;
            previousUnit = unit;
        }
        total += digit;
        if (total is <= 0 or > 9999) return null;
        string canonical = FormatStructuredCjk(total);
        return normalized == canonical || canonical.StartsWith('十') && normalized == "一" + canonical ? total : null;
    }

    private static string FormatStructuredCjk(int value)
    {
        const string digits = "零一二三四五六七八九";
        StringBuilder output = new();
        bool gap = false;
        foreach ((int divisor, string unit) in new[] { (1000, "千"), (100, "百"), (10, "十"), (1, "") })
        {
            int digit = value / divisor;
            value %= divisor;
            if (digit == 0) { if (output.Length > 0 && value > 0) gap = true; continue; }
            if (gap) { output.Append('零'); gap = false; }
            if (!(divisor == 10 && digit == 1 && output.Length == 0)) output.Append(digits[digit]);
            output.Append(unit);
        }
        return output.ToString();
    }
}
