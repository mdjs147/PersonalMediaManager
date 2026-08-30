using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>TMDB 候选四维加权打分器（标题 / 年份 / 热度 / 语言）</summary>
/// <remarks>
/// 纯静态无状态，供主管线（ProcessFileService）及任何需要对 TMDB 搜索候选择优的场景复用。
/// 修复背景：此前主管线盲取 Candidates[0]（TMDB 服务端默认序），Tmdb_Setting 的 4 个打分权重全仓无消费点。
/// 四个维度均归一到 [0,1]，按权重加权平均（权重和内部归一化，配置总和 ≠ 1 也不失真）：
///   · 标题：解析 / AI / 命中别名标题 与候选 Title / OriginalTitle 的归一化 Levenshtein 相似度，全组合取最高；
///   · 年份：双方都有年份时按差距阶梯衰减（差 0 → 1.0，±1 → 0.8，±2 → 0.4，更远 → 0）；
///     任一缺年不罚（记中性满分 1.0），避免「剧集常无解析年份」被系统性压到门槛之下；
///   · 热度：popularity 在本候选集内按最大值归一（全 0 视为无信息，全员中性 1.0）；
///   · 语言：候选原始语言命中偏好语言主标签，或产地命中偏好语言区域标签 → 1.0，否则 0
///     （偏好语言为空 / 候选无语言与产地信息时记中性 1.0，缺信息不罚）。
/// 排序稳定：同分保持 TMDB 服务端原始顺序（LINQ OrderBy 稳定排序）。
/// </remarks>
public static class TmdbCandidateScorer
{
    /// <summary>对候选集打分并按综合得分降序排序（同分保持服务端原始顺序）</summary>
    /// <param name="candidates">TMDB 搜索候选集（空集返回空列表）</param>
    /// <param name="parsedTitles">参与标题比对的解析标题集（规则 / AI / 命中别名；null 与空白项自动忽略）</param>
    /// <param name="parsedYear">解析年份；null 表示未知（年份维度对全部候选记中性满分）</param>
    /// <param name="weights">四维权重（来自 Tmdb_Setting；负值按 0 计，总和 ≤ 0 回退默认权重）</param>
    /// <param name="preferredLanguage">偏好语言 BCP-47 标签（如 zh-CN）；null / 空白时语言维度记中性满分</param>
    public static IReadOnlyList<TmdbCandidateScore> Rank(
        IReadOnlyList<TmdbCandidate> candidates,
        IReadOnlyList<string?> parsedTitles,
        int? parsedYear,
        TmdbScoreWeights weights,
        string? preferredLanguage = null)
    {
        if (candidates.Count == 0) return [];

        // 权重防御：负值按 0 计；总和 ≤ 0（全 0 的无效配置）回退默认权重，避免除零 / 全员 0 分
        double wTitle = Math.Max(0, weights.Title);
        double wYear = Math.Max(0, weights.Year);
        double wPop = Math.Max(0, weights.Popularity);
        double wLang = Math.Max(0, weights.Language);
        double total = wTitle + wYear + wPop + wLang;
        if (total <= 0)
        {
            (wTitle, wYear, wPop, wLang) = (TmdbScoreWeights.Default.Title, TmdbScoreWeights.Default.Year,
                TmdbScoreWeights.Default.Popularity, TmdbScoreWeights.Default.Language);
            total = wTitle + wYear + wPop + wLang;
        }

        List<string> titles = parsedTitles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct()
            .ToList();

        double maxPopularity = candidates.Max(c => c.Popularity);
        (string? langPrimary, string? langRegion) = SplitLanguageTag(preferredLanguage);

        return candidates
            .Select(c => new TmdbCandidateScore(
                c,
                (wTitle * TitleScore(titles, c)
                 + wYear * YearScore(parsedYear, c.Year)
                 + wPop * PopularityScore(c.Popularity, maxPopularity)
                 + wLang * LanguageScore(c, langPrimary, langRegion)) / total))
            .OrderByDescending(s => s.Score) // LINQ 稳定排序：同分保持服务端原始顺序
            .ToList();
    }

    /// <summary>标题维度：所有解析标题 × 候选（Title / OriginalTitle）组合的归一化相似度取最高</summary>
    /// <remarks>取较高者的本因：候选 Title 是 zh-CN 本地化名、OriginalTitle 是原产地名，解析标题可能命中其一。</remarks>
    private static double TitleScore(IReadOnlyList<string> parsedTitles, TmdbCandidate candidate)
    {
        double best = 0;
        foreach (string title in parsedTitles)
        {
            best = Math.Max(best, TitleSimilarity.Ratio(title, candidate.Title));
            best = Math.Max(best, TitleSimilarity.Ratio(title, candidate.OriginalTitle));
        }
        return best;
    }

    /// <summary>年份维度：差距阶梯衰减；任一侧缺年不罚（中性满分）</summary>
    /// <remarks>±1 年仍给高分：上映年 / 首播年与文件标注常差一年（地区上映差、跨年播出），不应重罚。</remarks>
    private static double YearScore(int? parsedYear, int? candidateYear)
    {
        if (parsedYear is null || candidateYear is null) return 1.0;
        return Math.Abs(parsedYear.Value - candidateYear.Value) switch
        {
            0 => 1.0,
            1 => 0.8,
            2 => 0.4,
            _ => 0.0,
        };
    }

    /// <summary>热度维度：popularity 在候选集内按最大值归一；全 0 视为无信息（全员中性满分）</summary>
    private static double PopularityScore(double popularity, double maxPopularity)
    {
        if (maxPopularity <= 0) return 1.0;
        return Math.Clamp(popularity / maxPopularity, 0.0, 1.0);
    }

    /// <summary>语言/产地维度：原始语言命中偏好主标签或产地命中偏好区域 → 1.0；缺信息中性满分</summary>
    private static double LanguageScore(TmdbCandidate candidate, string? langPrimary, string? langRegion)
    {
        // 无偏好语言（未配置）→ 维度对全员中性，不影响排序与绝对得分
        if (langPrimary is null) return 1.0;

        bool hasLangInfo = !string.IsNullOrWhiteSpace(candidate.OriginalLanguage);
        bool hasCountryInfo = candidate.OriginCountry is { Count: > 0 };
        if (!hasLangInfo && !hasCountryInfo) return 1.0; // 候选缺语言与产地信息：缺信息不罚

        if (hasLangInfo && string.Equals(candidate.OriginalLanguage, langPrimary, StringComparison.OrdinalIgnoreCase))
            return 1.0;
        if (hasCountryInfo && langRegion is not null
            && candidate.OriginCountry!.Any(c => string.Equals(c, langRegion, StringComparison.OrdinalIgnoreCase)))
            return 1.0;
        return 0.0;
    }

    /// <summary>拆 BCP-47 语言标签为（主语言子标签, 区域子标签）；如 zh-CN → (zh, CN)，ja → (ja, null)</summary>
    private static (string? Primary, string? Region) SplitLanguageTag(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return (null, null);
        string[] parts = languageTag.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (null, null);
        return (parts[0], parts.Length > 1 ? parts[^1] : null);
    }
}

/// <summary>单个候选的综合打分结果</summary>
/// <param name="Candidate">原候选</param>
/// <param name="Score">四维加权综合得分 [0,1]</param>
public sealed record TmdbCandidateScore(TmdbCandidate Candidate, double Score);

/// <summary>TMDB 候选打分四维权重（与 Tmdb_Setting.ScoreWeight* 一一对应）</summary>
/// <remarks>权重总和由打分器内部归一化，不强制 = 1；Default 与 Tmdb_Setting 种子默认值保持一致。</remarks>
public sealed record TmdbScoreWeights(double Title, double Year, double Popularity, double Language)
{
    /// <summary>默认权重（与 Tmdb_Setting 种子一致：0.5 / 0.3 / 0.1 / 0.1）</summary>
    public static TmdbScoreWeights Default { get; } = new(0.5, 0.3, 0.1, 0.1);
}
