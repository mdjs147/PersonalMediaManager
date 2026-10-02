using System.Text.Json.Serialization;

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
/// <param name="SeasonTitle">篇章原文候选；不能独立证明季号、类型或系列身份，供目录核验或人工对照</param>
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
    IReadOnlyList<string>? AlternativeTitles = null,
    IReadOnlyList<RuleFieldEvidence>? FieldEvidence = null,
    IReadOnlyList<string>? Conflicts = null,
    IReadOnlyList<string>? RejectedFields = null,
    bool ForceType = false,
    bool HasIdentityEvidence = true,
    RuleNamingEvidence? NamingEvidence = null,
    IReadOnlyList<RuleNumberingEvidence>? NumberingEvidence = null,
    RuleExecutionDiagnostics? Diagnostics = null);

/// <summary>规则字段的可复核局部证据（不含绝对路径）</summary>
public sealed record RuleFieldEvidence(string Field, int Value, string Source, string Token);

/// <summary>保留命名原文、版本信息及尚待核验的解释</summary>
public sealed record RuleNamingEvidence(string OriginalTitle, IReadOnlyList<string> EditionTags,
    int? SeasonCandidate = null, string? SeasonMappingSource = null,
    IReadOnlyList<string>? Uncertainties = null,
    IReadOnlyList<RuleTitleVariant>? TitleVariants = null,
    RuleSourceNumberCandidate? NumberingCandidate = null,
    IReadOnlyList<RuleTitleCandidateDecision>? TitleCandidateDecisions = null);

/// <summary>标题候选的采纳或保留原因</summary>
public sealed record RuleTitleCandidateDecision(string Candidate, string Decision, string Reason,
    string? Source = null, int? SegmentIndex = null, int? Start = null, int? Length = null,
    string? Token = null);

/// <summary>原文可核查的完整标题片段，不将文字脚本猜作语言</summary>
/// <remarks>Start / Length 为 Source 所指完整原始字符串的 UTF-16 索引；Token 必须与该区间完全相等。AliasOf 仅关联同处命名的候选，不证明已核实译名或同一作品；Language 无独立证据时为空。</remarks>
public sealed record RuleTitleVariant(string Title, string ScriptHint, string? Language,
    string Source, int? SegmentIndex, int Start, int Length, string Token, string? AliasOf);

/// <summary>可核查的来源编号候选，不等于当前集或官方集序</summary>
public sealed record RuleSourceNumberCandidate(int Value, string Source, int Start, int Length,
    string Token, string Interpretation);

/// <summary>来源编号的语义空间，不隐含官方集序映射</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleNumberingKind
{
    Season, LocalEpisode, InclusiveRange, ExplicitList, FractionalEpisode,
    AirDate, ShortAirDate, Volume, Disc, Part, Cour, Issue, Absolute,
    ReleaseRevision, ContentKind, InvalidNumber, TechnicalNumber,
}

/// <summary>局部证据的采纳状态</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleEvidenceState { Missing, Accepted, Candidate, Rejected, Conflict }

/// <summary>具有原文区间的编号、日期及内容种类证据</summary>
/// <remarks>局部 Source 为 FileName 或 RelativeSegment；SegmentIndex 是外到内目录数组的实际位置。跨段用户捕获只能为拒绝候选：RelativePath 区间针对斜杠拼接的所有目录及文件名，FullPath 区间针对旧规则作用域的直接父目录与文件名拼接，均不指绝对路径。Start/Length 为完整原文 UTF-16 区间，Token 必须等于反取结果。RuleKey 是稳定的语义规则家族标识，不宣称精确正则身份。Accepted 只证明本地语法，不证明官方集序。</remarks>
public sealed record RuleNumberingEvidence(RuleNumberingKind Kind, RuleEvidenceState State,
    string Field, string Source, int? SegmentIndex, int Start, int Length, string Token,
    int? Value = null, int? End = null, IReadOnlyList<int>? Values = null,
    string? TextValue = null, string? Reason = null, string? RuleKey = null);
