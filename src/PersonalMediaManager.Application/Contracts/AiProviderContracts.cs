namespace PersonalMediaManager.Application.Contracts;

/// <summary>调用端配置（每次从聚合 ParseAiProvider 投影得到，避免 External 反向引 Persistence）</summary>
/// <param name="BaseUrl">完整 URL；Ollama 默认 http://localhost:11434</param>
/// <param name="ApiKey">明文 ApiKey；调用前已由 IProtectedFieldService 解密；Ollama 可为空</param>
/// <param name="Model">模型名（如 llama3 / qwen-plus / deepseek-chat）</param>
/// <param name="TimeoutSeconds">单次请求总超时（含建立连接 + 首字节 + 收完整 body），默认 30s</param>
/// <param name="UseProxy">是否通过代理访问；实际是否走代理还要看系统代理总开关 + bypass 命中</param>
/// <param name="IsFree">是否免费档节点；true 时协议策略豁免 1 req/s 节流（免费档不计费、无配额压力，无需限速）</param>
/// <param name="StructuredJson">该端点是否支持结构化 JSON 输出；true 时 JsonMode 请求会下发 response_format=json_object，false 时仅靠 system prompt 约束（兼容不识别 response_format 的代理）</param>
/// <param name="ExtraOptions">JSON 扩展参数（temperature/topP 等）原文透传；协议策略按需解析合并，null 表示无扩展</param>
public sealed record AiProviderEndpoint(
    string BaseUrl,
    string? ApiKey,
    string Model,
    int TimeoutSeconds = 30,
    bool UseProxy = false,
    bool IsFree = false,
    bool StructuredJson = true,
    string? ExtraOptions = null);

/// <summary>解析入参（原始文件名 + 可选父目录名 + 可选规则引擎提取的提示词 + 可选监控根→文件的完整路径段）</summary>
/// <param name="FileName">仅文件名（不含目录、含后缀）</param>
/// <param name="ParentFolderName">直接父目录名（保留向后兼容；新调用方建议改用 RelativeSegments）</param>
/// <param name="RuleHintTitle">规则引擎提取的标题提示（仅供 AI 参考）</param>
/// <param name="RuleHintYear">规则引擎提取的年份提示（仅供 AI 参考）</param>
/// <param name="RelativeSegments">监控根（不含）→ 文件本身（不含）之间的所有目录段，外层 → 内层。可空表示无路径上下文（仅文件名）。优先级高于 ParentFolderName：AiPromptHelpers 若发现该字段非空，会忽略 ParentFolderName</param>
/// <param name="RuleHintType">规则引擎初判媒体类型（"movie"/"tv"/"unknown"，"unknown" 不入 prompt）；供 AI 对比修正 movie/tv 判断</param>
/// <param name="RuleHintSeason">规则引擎初判季号（仅供参考）；规则的 Season NN / SxxExx 数字匹配常比剧名识别更可靠</param>
/// <param name="RuleHintEpisode">规则引擎初判集号（仅供参考；双集为起始集）</param>
/// <param name="RuleHintEpisodeEnd">规则引擎初判双集结束集（仅供参考；单集为 null）</param>
public sealed record AiParseRequest(
    string FileName,
    string? ParentFolderName = null,
    string? RuleHintTitle = null,
    int? RuleHintYear = null,
    IReadOnlyList<string>? RelativeSegments = null,
    string? RuleHintType = null,
    int? RuleHintSeason = null,
    int? RuleHintEpisode = null,
    int? RuleHintEpisodeEnd = null);

/// <summary>AI 解析结果（与 ParseTask 决策矩阵对齐）</summary>
/// <param name="Title">主标题（中文优先，英文回退）</param>
/// <param name="Year">年份（无法识别则 null）</param>
/// <param name="MediaType">"movie" 或 "tv"（AI 必须二选一）</param>
/// <param name="Season">季号（仅 tv 类型有意义；movie / 单集番剧可为 null）</param>
/// <param name="Episode">集号（仅 tv 类型有意义）；双集合并时为起始集</param>
/// <param name="EpisodeEnd">双集/多集合并的结束集；单集 / movie 为 null</param>
/// <param name="Confidence">0~1，AI 自评分；&lt; provider 的 ConfidenceThreshold（默认 0.7）时上层视为「结果不满意」→ 升级到下一级</param>
/// <param name="SearchAliases">TMDB 检索别名候选（原名/日文/英文官方译名/罗马音/拼音）；中文 Title 在 TMDB 查不到时逐个兜底检索（国漫/日漫主条目常为原名）。可空，向后兼容</param>
public sealed record AiParseResult(
    string Title,
    int? Year,
    string MediaType,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    double Confidence,
    IReadOnlyList<string>? SearchAliases = null)
{
    /// <summary>结果是否达到可接受质量（置信度达标 + 标题非空）</summary>
    /// <remarks>
    /// 质量门只看「置信度 + 标题」两项可用性指标：
    /// - Confidence ≥ threshold（AI 自评把握足够）
    /// - Title 非空（ParseContent 已保证，此处双重防御）
    /// 季 / 集完整性不在此判定——剧集缺季集由下游 ProcessFileService 的单季自动补季 + ParseIncomplete 人工守护处理，
    /// 不在升级层重复（缺季集多半是文件名本身没信息，换更高级 AI 也补不出，升级是浪费）。
    /// </remarks>
    public bool IsAcceptable(double threshold) =>
        Confidence >= threshold && !string.IsNullOrWhiteSpace(Title);
}

/// <summary>AI 解析门面单次调用结果（结构化结果 + 诊断原文 + token 用量）</summary>
/// <remarks>
/// 在 <see cref="AiParseResult"/> 之外携带监控诊断维度：请求/响应原文与厂商 token 用量，供编排层（AiCallOrchestrator）落 Audit_AiCall。
/// 仅「成功 / 低置信软失败」路径经此类型返回（HTTP 成功，含可接受与不可接受置信度两种）；
/// 接口故障路径（抛 AiProvider*Exception）不经此类型，其诊断原文经 <see cref="AiCallDiagnostics"/>（Exception.Data）携带。
/// </remarks>
public sealed record AiParseOutcome(
    AiParseResult Result,
    string? RequestText = null,
    string? ResponseText = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

/// <summary>瞬时错误：不消耗级数额度，允许 AiCallChain.RecordTransientError 后内部短重试 1 次</summary>
public sealed class AiProviderTransientException : Exception
{
    public AiProviderTransientException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>限流 / 配额错误（HTTP 429 / 配额耗尽）：消耗 1 级额度且不重试，立即升级到更高级 AI</summary>
/// <remarks>
/// 与 <see cref="AiProviderTransientException"/> 的区别：瞬时错误是临时网络抖动，同 provider 短重试有意义；
/// 限流是「该 provider 已达调用上限」，本文件在同一 provider 再重试也是同样结果，直接升级到更高级 AI 更划算。
/// 这正落实「接口达到上限时自动升级」的需求。
/// </remarks>
public sealed class AiProviderRateLimitException : Exception
{
    public AiProviderRateLimitException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>逻辑错误：消耗 1 级额度；4xx / JSON 解析失败 / schema 不符均归此类</summary>
public sealed class AiProviderLogicalException : Exception
{
    public AiProviderLogicalException(string message, int? httpStatus = null, Exception? inner = null) : base(message, inner)
    {
        HttpStatus = httpStatus;
    }

    public int? HttpStatus { get; }
}
