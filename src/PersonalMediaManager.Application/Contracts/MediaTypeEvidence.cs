using System.Text.RegularExpressions;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>原文发行形式线索；不把题材、年份或标题常识当类型证明</summary>
public static class MediaTypeEvidence
{
    public static bool HasMovieSupport(AiParseRequest request)
    {
        // 明确的发行形式标签才直接支持电影；一般标题中的 movie 一词不足。
        IReadOnlyList<string> directories = request.RelativeSegments
            ?? (request.ParentFolderName is { } parent ? [parent] : []);
        if (HasReleaseMarker(request.FileName) || directories.Any(HasReleaseMarker)) return true;
        // The Movie 也可能是标题文字，须与独立、纯电影目录分类同时出现。
        return Regex.IsMatch(request.FileName, @"(?i)(?<![\p{L}\d])The[ ._]Movie(?![\p{L}\d])")
            && directories.Any(segment => Regex.IsMatch(segment.Trim(), @"(?i)^(?:Movies?|电影|電影|电影版|電影版)$"));
    }

    public static bool HasTvSupport(AiParseRequest request) =>
        Regex.IsMatch(request.FileName,
            @"(?ix)(?:^|[\[\(（【 _.\-])(?:TV[ ._]Series|电视剧|電視劇|TVアニメ)(?=$|[\]\)）】 _.\-])");

    private static bool HasReleaseMarker(string source) => Regex.IsMatch(source,
        @"(?ix)(?:^|[\[\(（【 _.\-])(?:剧场版|劇場版|电影版|電影版|Gekijouban)(?=$|[\]\)）】 _.\-])");
}
