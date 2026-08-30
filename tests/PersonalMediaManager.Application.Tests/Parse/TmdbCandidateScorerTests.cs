using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

/// <summary>TmdbCandidateScorer — 四维加权打分与排序</summary>
/// <remarks>
/// 覆盖矩阵：
///   1. 同名不同剧（重制版）按年份择优
///   2. 标题相似度主导默认权重排序
///   3. 权重改变排序结果（权重真实生效）
///   4. 缺年不罚（中性满分，不被系统性压分）
///   5. OriginalTitle 参与取较高者（跨语言条目）
///   6. 多解析标题（别名）取最高
///   7. 热度按集内最大值归一
///   8. 语言 / 产地匹配加分
///   9. 同分稳定排序保持服务端原序
///  10. 边界：空候选集 / 全 0 权重回退默认
/// </remarks>
public sealed class TmdbCandidateScorerTests
{
    private static TmdbCandidate Candidate(
        int id, string? title, int? year, double popularity = 10,
        string? originalTitle = null, string? language = "en", string[]? countries = null)
        => new(id, "movie", title, originalTitle, year, popularity, language, countries ?? ["US"], null, null);

    // ---------- 1. 同名不同剧（重制版场景）：年份是判别器 ----------
    [Fact]
    public void SameTitle_DifferentYear_PrefersCloserYear()
    {
        // 同名重制（如 1990 原版 vs 2017 重制）：标题同分，年份差决定排序
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "IT", 1990),
            Candidate(2, "IT", 2017),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(
            candidates, ["IT"], parsedYear: 2017, TmdbScoreWeights.Default);

        ranked[0].Candidate.Id.Should().Be(2, "解析年份 2017 应优选 2017 版");
        ranked[0].Score.Should().BeGreaterThan(ranked[1].Score);
    }

    // ---------- 2. 标题相似度主导（默认权重 0.5 最大）----------
    [Fact]
    public void TitleSimilarity_Dominates_With_DefaultWeights()
    {
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "Totally Unrelated", 1985),
            Candidate(2, "The Matrix", 1999),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(
            candidates, ["The Matrix"], 1999, TmdbScoreWeights.Default, preferredLanguage: "zh-CN");

        ranked[0].Candidate.Id.Should().Be(2);
        ranked[0].Score.Should().BeGreaterThan(0.8, "标题 + 年份全中应得高分");
        ranked[1].Score.Should().BeLessThan(0.5, "标题不相似 + 年份偏差大的候选不应过多候选门槛");
    }

    // ---------- 3. 权重影响排序（权重真实生效，非死旋钮）----------
    [Fact]
    public void Weights_Change_Ordering()
    {
        // 标题全中但年份远 vs 标题不沾边但年份全中：默认权重选前者，「只看年份」权重选后者
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "Exact Title", 1990),
            Candidate(2, "Zzz Unrelated", 2020),
        ];
        string?[] titles = ["Exact Title"];

        IReadOnlyList<TmdbCandidateScore> byDefault = TmdbCandidateScorer.Rank(
            candidates, titles, 2020, TmdbScoreWeights.Default);
        IReadOnlyList<TmdbCandidateScore> byYearOnly = TmdbCandidateScorer.Rank(
            candidates, titles, 2020, new TmdbScoreWeights(Title: 0, Year: 1.0, Popularity: 0, Language: 0));

        byDefault[0].Candidate.Id.Should().Be(1, "默认权重下标题相似度占大头");
        byYearOnly[0].Candidate.Id.Should().Be(2, "只看年份的权重下应翻转排序");
        byYearOnly[0].Score.Should().Be(1.0);
        byYearOnly[1].Score.Should().Be(0.0);
    }

    // ---------- 4. 缺年不罚：任一侧无年份 → 年份维度中性满分 ----------
    [Fact]
    public void MissingYear_NotPenalized()
    {
        // 解析侧缺年（剧集文件名常无年份）：有年与无年的同标题候选得分一致
        List<TmdbCandidate> withYear = [Candidate(1, "Show", 2020)];
        List<TmdbCandidate> withoutYear = [Candidate(2, "Show", year: null)];

        double scoreWithYear = TmdbCandidateScorer.Rank(withYear, ["Show"], parsedYear: null, TmdbScoreWeights.Default)[0].Score;
        double scoreWithoutYear = TmdbCandidateScorer.Rank(withoutYear, ["Show"], parsedYear: null, TmdbScoreWeights.Default)[0].Score;

        scoreWithYear.Should().Be(scoreWithoutYear, "任一侧缺年 → 年份维度一律中性满分，不奖不罚");
    }

    [Fact]
    public void YearDecay_Steps_By_Distance()
    {
        // 年份阶梯衰减：0 差 > ±1 > ±2 > 更远（上映年 / 首播年差一年很常见，不应重罚）
        TmdbScoreWeights yearOnly = new(0, 1.0, 0, 0);
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "X", 2020),
            Candidate(2, "X", 2019),
            Candidate(3, "X", 2018),
            Candidate(4, "X", 2000),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(candidates, ["X"], 2020, yearOnly);

        ranked.Select(r => r.Candidate.Id).Should().ContainInOrder(1, 2, 3, 4);
        ranked[0].Score.Should().Be(1.0);
        ranked[1].Score.Should().Be(0.8);
        ranked[2].Score.Should().Be(0.4);
        ranked[3].Score.Should().Be(0.0);
    }

    // ---------- 5. OriginalTitle 参与比对取较高者（跨语言条目）----------
    [Fact]
    public void OriginalTitle_Considered_TakesHigher()
    {
        // TMDB zh-CN 本地化标题是中文、原名是英文：英文解析名靠 OriginalTitle 命中
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "绝命毒师", 2008, originalTitle: "Breaking Bad"),
        ];

        double score = TmdbCandidateScorer.Rank(candidates, ["Breaking Bad"], 2008, TmdbScoreWeights.Default)[0].Score;

        score.Should().BeGreaterThan(0.8, "解析名与 OriginalTitle 完全一致应拿满标题分");
    }

    // ---------- 6. 多解析标题（规则 / AI / 别名）全组合取最高 ----------
    [Fact]
    public void MultipleParsedTitles_TakeMax()
    {
        // 中文解析名与候选不沾边，但命中检索的别名（原名）一致 → 标题分取别名组合的最高值
        List<TmdbCandidate> candidates = [Candidate(1, "Ousama Ranking", 2021)];

        double withAlias = TmdbCandidateScorer.Rank(
            candidates, ["国王排名", "Ousama Ranking"], 2021, TmdbScoreWeights.Default)[0].Score;
        double withoutAlias = TmdbCandidateScorer.Rank(
            candidates, ["国王排名"], 2021, TmdbScoreWeights.Default)[0].Score;

        withAlias.Should().BeGreaterThan(withoutAlias, "纳入别名后标题维度应取更高的匹配值");
        withAlias.Should().BeGreaterThan(0.8);
    }

    // ---------- 7. 热度集内最大值归一 ----------
    [Fact]
    public void Popularity_Normalized_Within_CandidateSet()
    {
        TmdbScoreWeights popOnly = new(0, 0, 1.0, 0);
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "X", 2020, popularity: 5),
            Candidate(2, "X", 2020, popularity: 50),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(candidates, ["X"], 2020, popOnly);

        ranked[0].Candidate.Id.Should().Be(2);
        ranked[0].Score.Should().Be(1.0, "集内最热的候选归一为 1.0");
        ranked[1].Score.Should().BeApproximately(0.1, 1e-9, "5 / 50 = 0.1");
    }

    [Fact]
    public void Popularity_AllZero_Is_Neutral()
    {
        // 全 0 热度（如合成候选）视为无信息：全员中性满分，不拖低绝对得分
        TmdbScoreWeights popOnly = new(0, 0, 1.0, 0);
        List<TmdbCandidate> candidates = [Candidate(1, "X", 2020, popularity: 0)];

        TmdbCandidateScorer.Rank(candidates, ["X"], 2020, popOnly)[0].Score.Should().Be(1.0);
    }

    // ---------- 8. 语言 / 产地匹配 ----------
    [Fact]
    public void Language_Match_Beats_Mismatch()
    {
        TmdbScoreWeights langOnly = new(0, 0, 0, 1.0);
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "X", 2020, language: "en", countries: ["US"]),
            Candidate(2, "X", 2020, language: "zh", countries: ["CN"]),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(
            candidates, ["X"], 2020, langOnly, preferredLanguage: "zh-CN");

        ranked[0].Candidate.Id.Should().Be(2, "原始语言命中偏好语言主标签应得满分");
        ranked[0].Score.Should().Be(1.0);
        ranked[1].Score.Should().Be(0.0);
    }

    [Fact]
    public void OriginCountry_Match_Counts_As_Language_Hit()
    {
        // 粤语片 OriginalLanguage=cn 但产地 HK/CN：产地命中区域子标签同样算匹配
        TmdbScoreWeights langOnly = new(0, 0, 0, 1.0);
        List<TmdbCandidate> candidates = [Candidate(1, "X", 2020, language: "cn", countries: ["CN", "HK"])];

        TmdbCandidateScorer.Rank(candidates, ["X"], 2020, langOnly, "zh-CN")[0].Score
            .Should().Be(1.0, "产地含 CN 命中 zh-CN 的区域子标签");
    }

    [Fact]
    public void NoPreferredLanguage_Is_Neutral()
    {
        TmdbScoreWeights langOnly = new(0, 0, 0, 1.0);
        List<TmdbCandidate> candidates = [Candidate(1, "X", 2020, language: "en")];

        TmdbCandidateScorer.Rank(candidates, ["X"], 2020, langOnly, preferredLanguage: null)[0].Score
            .Should().Be(1.0, "未配置偏好语言 → 语言维度中性，不影响得分");
    }

    // ---------- 9. 同分稳定排序：保持服务端原始顺序 ----------
    [Fact]
    public void EqualScores_Preserve_Server_Order()
    {
        List<TmdbCandidate> candidates =
        [
            Candidate(11, "Same", 2020, popularity: 10),
            Candidate(22, "Same", 2020, popularity: 10),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(
            candidates, ["Same"], 2020, TmdbScoreWeights.Default);

        ranked.Select(r => r.Candidate.Id).Should().ContainInOrder(11, 22);
        ranked[0].Score.Should().Be(ranked[1].Score);
    }

    // ---------- 10. 边界 ----------
    [Fact]
    public void EmptyCandidates_Returns_Empty()
    {
        TmdbCandidateScorer.Rank([], ["X"], 2020, TmdbScoreWeights.Default).Should().BeEmpty();
    }

    [Fact]
    public void AllZero_Weights_FallBack_To_Default()
    {
        // 全 0 权重是无效配置：回退默认权重，不出现除零 / 全员 0 分
        List<TmdbCandidate> candidates =
        [
            Candidate(1, "Wrong", 1980),
            Candidate(2, "Right Title", 2020),
        ];

        IReadOnlyList<TmdbCandidateScore> ranked = TmdbCandidateScorer.Rank(
            candidates, ["Right Title"], 2020, new TmdbScoreWeights(0, 0, 0, 0));

        ranked[0].Candidate.Id.Should().Be(2);
        ranked[0].Score.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void NullOrBlank_ParsedTitles_Ignored()
    {
        List<TmdbCandidate> candidates = [Candidate(1, "Real Name", 2020)];

        double score = TmdbCandidateScorer.Rank(
            candidates, [null, "  ", "Real Name"], 2020, TmdbScoreWeights.Default)[0].Score;

        score.Should().BeGreaterThan(0.8, "null / 空白解析标题应被忽略，不拉低匹配");
    }
}
