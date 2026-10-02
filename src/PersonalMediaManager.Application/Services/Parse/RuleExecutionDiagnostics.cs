namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>有界规则执行轨迹，不包含完整路径或正则正文</summary>
public sealed record RuleExecutionDiagnostics(string Status, int RulesEvaluated, int MatchAttempts,
    int RegexCacheHits, double ElapsedMilliseconds, int BudgetMilliseconds, bool TraceTruncated,
    IReadOnlyList<RuleTraceEvent> Events,
    IReadOnlyList<string>? FaultReasons = null);

/// <summary>规则命中、跳过或拒绝的可追溯原因</summary>
public sealed record RuleTraceEvent(string RuleKey, string Outcome, string? Reason = null,
    long? RuleId = null, string? Scope = null, string? Source = null, int? SegmentIndex = null,
    string? PatternHash = null, double? ElapsedMilliseconds = null);
