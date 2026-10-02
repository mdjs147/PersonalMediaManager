using System.Globalization;
using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

internal sealed partial class RuleEngineService
{
    /// <summary>保留用户明确声明的来源编号空间及真实匹配区间</summary>
    private static IReadOnlyList<RuleNumberingEvidence> CollectUserNumberingCaptures(Match capture, string matchedInput,
        string matchedSource, int? matchedSegment, FileParseContext context, long ruleId)
    {
        List<RuleNumberingEvidence> result = [];
        List<(string Source, int? Segment, int Start, int Length)> spans = [];
        if (matchedSource == "FileName" && (matchedInput == context.FileName
            || matchedInput == Path.GetFileNameWithoutExtension(context.FileName)))
            spans.Add(("FileName", null, 0, matchedInput.Length));
        else if (matchedSource == "RelativeSegment" && matchedSegment is int segment
            && segment >= 0 && segment < context.RelativeSegments.Count && matchedInput == context.RelativeSegments[segment])
            spans.Add(("RelativeSegment", segment, 0, matchedInput.Length));
        else if (matchedSource == "RelativePath"
            && matchedInput == string.Join('/', context.RelativeSegments.Concat([context.FileName])))
        {
            int offset = 0;
            for (int i = 0; i < context.RelativeSegments.Count; i++)
            {
                spans.Add(("RelativeSegment", i, offset, context.RelativeSegments[i].Length));
                offset += context.RelativeSegments[i].Length + 1;
            }
            spans.Add(("FileName", null, offset, context.FileName.Length));
        }
        else if (matchedSource == "FullPath"
            && matchedInput == Path.Combine(context.DirectParentFolderName ?? string.Empty, context.FileName))
        {
            if (context.DirectParentFolderName is { Length: > 0 } parent)
                spans.Add(("RelativeSegment", context.RelativeSegments.Count - 1, 0, parent.Length));
            spans.Add(("FileName", null, matchedInput.Length - context.FileName.Length, context.FileName.Length));
        }

        foreach ((string name, RuleNumberingKind family) in new[]
        {
            ("airDate", RuleNumberingKind.AirDate), ("volume", RuleNumberingKind.Volume),
            ("disc", RuleNumberingKind.Disc), ("part", RuleNumberingKind.Part),
            ("cour", RuleNumberingKind.Cour), ("absolute", RuleNumberingKind.Absolute),
        })
        foreach (Capture value in capture.Groups[name].Captures)
        {
            if (value.Length == 0) continue;
            (string Source, int? Segment, int Start, int Length)[] local = spans
                .Where(s => value.Index >= s.Start && value.Index + value.Length <= s.Start + s.Length).ToArray();
            bool mapped = local.Length == 1;
            string source = mapped ? local[0].Source : matchedSource;
            int? sourceSegment = mapped ? local[0].Segment : matchedSegment;
            int start = mapped ? value.Index - local[0].Start : value.Index;
            string normalized = FoldStructuredWidth(value.Value);
            int? number = int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
            string? textValue = value.Value;
            RuleNumberingKind kind = family;
            RuleEvidenceState state = RuleEvidenceState.Candidate;
            string reason = "用户声明的来源编号不能直接作为季内集号";
            if (family == RuleNumberingKind.AirDate)
            {
                number = null;
                string digits = new(normalized.Where(char.IsAsciiDigit).ToArray());
                bool shortDate = digits.Length == 6;
                kind = shortDate ? RuleNumberingKind.ShortAirDate : RuleNumberingKind.AirDate;
                bool valid = DateOnly.TryParseExact(shortDate ? "20" + digits : digits, "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date);
                state = valid ? RuleEvidenceState.Candidate : RuleEvidenceState.Rejected;
                if (valid && !shortDate) textValue = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                reason = valid ? "用户播出日期候选需要目录映射，短日期的世纪仍未知" : "用户播出日期不是有效日历日期";
            }
            if (!mapped)
            {
                state = RuleEvidenceState.Rejected;
                reason = "用户捕获跨越路径段或无法映射为局部原文，仅保留匹配输入候选，禁止补入正典字段";
            }
            result.Add(new(kind, state, name, source, sourceSegment, start, value.Length, value.Value,
                number, TextValue: textValue, Reason: reason, RuleKey: $"user:{ruleId}:capture:{name}"));
        }
        return result;
    }
}
