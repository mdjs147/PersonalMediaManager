using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Aggregates.ParseTasks;

/// <summary>解析任务聚合（非持久化）— 封装 §3.3.2 决策矩阵</summary>
/// <remarks>
/// D1.2：把「规则置信度 + TMDB 候选数 + 特殊字符标记」决策矩阵集中到此聚合，
/// Application 服务（D7.1 ProcessFileService）只问 NextAction，不写决策分支。
/// 本聚合不入库：表示一次内存中的「解析尝试」，结果落到 MediaItem 上。
/// </remarks>
public sealed class ParseTask
{
    private ParseTask(double confidence, int? candidateCount, bool hasSpecialChars, double confidenceThreshold, int candidateThreshold)
    {
        Confidence = confidence;
        CandidateCount = candidateCount;
        HasSpecialChars = hasSpecialChars;
        ConfidenceThreshold = confidenceThreshold;
        CandidateThreshold = candidateThreshold;
    }

    /// <summary>规则引擎输出置信度（0~1）</summary>
    public double Confidence { get; }

    /// <summary>TMDB 候选数：null = 尚未查询（规则置信度 &lt; 阈值，直接走 AI）</summary>
    public int? CandidateCount { get; }

    /// <summary>命中「特殊字符规则」（中日韩混杂 / 特殊符号），强制走 AI</summary>
    public bool HasSpecialChars { get; }

    /// <summary>置信度阈值（默认 0.6，可在配置中调整）</summary>
    public double ConfidenceThreshold { get; }

    /// <summary>候选阈值 N（默认 3）</summary>
    public int CandidateThreshold { get; }

    /// <summary>第一次 TMDB 查询前：规则置信度 + 特殊字符判定</summary>
    public static ParseTask AfterRuleEngine(
        double confidence,
        bool hasSpecialChars,
        double confidenceThreshold = 0.6,
        int candidateThreshold = 3)
    {
        return new ParseTask(confidence, candidateCount: null, hasSpecialChars, confidenceThreshold, candidateThreshold);
    }

    /// <summary>第一次 TMDB 查询后：把候选数喂进来</summary>
    public static ParseTask AfterTmdbQuery(
        double confidence,
        int candidateCount,
        bool hasSpecialChars,
        double confidenceThreshold = 0.6,
        int candidateThreshold = 3)
    {
        return new ParseTask(confidence, candidateCount, hasSpecialChars, confidenceThreshold, candidateThreshold);
    }

    /// <summary>核心决策：根据 §3.3.2 决策矩阵推导 NextAction</summary>
    /// <remarks>
    /// 优先级（自上而下短路）：
    /// 1. 特殊字符 → CallAi（不论置信度 / 候选数）
    /// 2. 规则置信度 &lt; 阈值 → CallAi（不查 TMDB）
    /// 3. 尚未查 TMDB（CandidateCount == null）→ UseTmdb 先查
    /// 4. TMDB 候选 &gt; N 或 候选 == 0 → CallAi
    /// 5. TMDB 候选 ∈ [1, N] → UseTmdb 采用
    /// 第二次 TMDB 查询后的决策由 AfterAiRetmdbQuery 处理（候选 ≤ N → UseTmdb；否则 SendToReview）。
    /// </remarks>
    public NextAction DecideAfterFirstTmdb()
    {
        if (HasSpecialChars) return NextAction.CallAi;
        if (Confidence < ConfidenceThreshold) return NextAction.CallAi;

        if (CandidateCount is null)
            return NextAction.UseTmdb; // 尚未查 TMDB，先查
        if (CandidateCount.Value == 0 || CandidateCount.Value > CandidateThreshold)
            return NextAction.CallAi;
        return NextAction.UseTmdb;
    }

    /// <summary>AI 兜底后第二次 TMDB 查询后的决策</summary>
    /// <remarks>
    /// AI 后的二次 TMDB：候选 ≤ N → 采用；其他（&gt; N 或零结果）→ 人工确认队列。
    /// 本方法不再考虑置信度（AI 的输出视为权威），仅看候选数。
    /// </remarks>
    public static NextAction DecideAfterAiRetmdb(int candidateCount, int candidateThreshold = 3)
    {
        if (candidateCount >= 1 && candidateCount <= candidateThreshold)
            return NextAction.UseTmdb;
        return NextAction.SendToReview;
    }
}

/// <summary>解析下一步动作</summary>
public enum NextAction
{
    /// <summary>采用 TMDB 结果进入 Classifying</summary>
    UseTmdb = 1,

    /// <summary>调 AI 兜底解析</summary>
    CallAi = 2,

    /// <summary>送人工确认队列</summary>
    SendToReview = 3,
}
