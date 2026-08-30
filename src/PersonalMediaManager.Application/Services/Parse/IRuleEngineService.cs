namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>规则引擎服务契约（D7.2 实现）</summary>
/// <remarks>
/// 输入：FileParseContext（文件名 + 监控根→文件之间的所有路径段，外层 → 内层）；
/// 输出：标题 / 类型 / 年份 / 季集（含双集合并 EpisodeEnd） + 置信度 + 特殊字符标记。
///
/// 实现规则：用户自定义 Parse_Rule 按 Priority 升序 + 内置兜底规则匹配；
/// 命名捕获 title / year / season / episode / episodeEnd 优先，缺省走原始文件名 stem 作为 title 兜底。
/// 完全无规则命中时返回低置信度结果（Confidence=0），让 ProcessFileService 直走 AI。
///
/// 多层路径段处理：规则的 Scope 字段决定 input 范围：
///   FileName     → 仅文件名（不含后缀）
///   ParentFolder → 直接父目录段（RelativeSegments 末尾）
///   FullPath     → 兼容旧值：Path.Combine(directParentFolder, fileName)
///   AllAncestors → 内层→外层依次匹配（先文件名，后父级、祖父级，按需补字段）
///   RelativePath → 所有 RelativeSegments + 文件名一起拼接（用 / 作分隔符标准化）
/// </remarks>
public interface IRuleEngineService
{
    Task<RuleParseResult> ParseAsync(FileParseContext context, CancellationToken ct = default);
}

/// <param name="Title">解析出的主标题（never null；无命中时回退到 fileName stem）</param>
/// <param name="Year">年份；无法识别 null</param>
/// <param name="MediaType">"movie" / "tv" / "unknown"</param>
/// <param name="Season">季号（剧集）</param>
/// <param name="Episode">集号（剧集）；双集/多集合并时为起始集</param>
/// <param name="EpisodeEnd">双集/多集合并的结束集（如 S01E08-E09 → EpisodeEnd=9）；单集为 null</param>
/// <param name="Confidence">综合置信度 0~1（基础分 + ConfidenceBonus，上限 1.0）</param>
/// <param name="HasSpecialChars">命中特殊字符规则（中日韩混杂 / 罕见符号）→ 强制走 AI</param>
/// <param name="MatchedRuleId">命中的 Parse_Rule 主键；未命中任何规则为 null</param>
/// <param name="SeasonTitle">季的篇章标题（如「锻刀村篇」），以篇章名标识季的番剧用；无则 null。供人工 / 自动与 TMDB 季名对照</param>
/// <param name="AlternativeTitles">
/// 本地备选搜索标题（主标题之外的候选，按命中希望降序，CJK 段优先）：主标题的混排拆分子段
/// （CJK 段 / 拉丁词组段）+ 其余路径层提取出的标题及其拆分段。供 ProcessFileService 在
/// 「主标题 TMDB 不中 / 混排 / 低置信」将触发 AI 兜底之前逐个重搜 TMDB——命中即免走 AI。
/// 无备选时为 null 或空列表。
/// </param>
public sealed record RuleParseResult(
    string Title,
    int? Year,
    string MediaType,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    double Confidence,
    bool HasSpecialChars,
    long? MatchedRuleId,
    string? SeasonTitle = null,
    IReadOnlyList<string>? AlternativeTitles = null);
