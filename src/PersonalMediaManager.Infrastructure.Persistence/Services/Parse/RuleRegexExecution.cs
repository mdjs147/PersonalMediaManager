using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

/// <summary>共享正则选项、容量缓存和每次规则计算预算</summary>
internal static class RuleRegexExecution
{
    internal const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);
    private const int CacheCapacity = 512;
    private const int MaximumMatches = 256;
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> CacheOrder = new();
    private static readonly AsyncLocal<Run?> Active = new();

    internal static Run Begin(CancellationToken ct) => new(ct);
    internal static string PatternHash(string pattern) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pattern))).ToLowerInvariant();

    internal static Regex Get(string pattern)
    {
        if (pattern.Length > 8192) throw new ArgumentException("正则长度超过安全执行上限");
        if (Cache.TryGetValue(pattern, out Regex? found))
        {
            if (Active.Value is { } run) run.RegexCacheHits++;
            return found;
        }
        Regex compiled = new(pattern, Options, MatchTimeout);
        if (Cache.TryAdd(pattern, compiled))
        {
            CacheOrder.Enqueue(pattern);
            while (Cache.Count > CacheCapacity && CacheOrder.TryDequeue(out string? oldest)) Cache.TryRemove(oldest, out _);
        }
        return compiled;
    }

    internal static Match Match(Regex expression, string input)
    {
        if (!Allowed(input)) return System.Text.RegularExpressions.Match.Empty;
        try { return expression.Match(input); }
        catch (RegexMatchTimeoutException) { Active.Value?.Incomplete("RegexTimeout"); return System.Text.RegularExpressions.Match.Empty; }
    }

    internal static IReadOnlyList<Match> Matches(Regex expression, string input)
    {
        if (!Allowed(input)) return [];
        try
        {
            List<Match> result = [];
            foreach (Match match in expression.Matches(input))
            {
                if (Active.Value?.CanContinue() == false) break;
                if (result.Count >= MaximumMatches) { Active.Value?.Incomplete("RegexMatchLimit"); break; }
                result.Add(match);
            }
            return result;
        }
        catch (RegexMatchTimeoutException) { Active.Value?.Incomplete("RegexTimeout"); return []; }
    }

    internal static string Replace(Regex expression, string input, string replacement)
    {
        if (!Allowed(input)) return input;
        try { return expression.Replace(input, replacement); }
        catch (RegexMatchTimeoutException) { Active.Value?.Incomplete("RegexTimeout"); return input; }
    }

    private static bool Allowed(string input)
    {
        Run? run = Active.Value;
        if (run?.CanContinue() == false) return false;
        if (input.Length > 32768) { run?.Incomplete("RegexInputLimit"); return false; }
        if (run is not null) run.MatchAttempts++;
        return true;
    }

    /// <summary>每次解析独立的取消、总预算和有界轨迹</summary>
    internal sealed class Run : IDisposable
    {
        private const int Budget = 2000;
        private const int TraceLimit = 128;
        private readonly CancellationToken _ct;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Run? _prior;
        private readonly List<RuleTraceEvent> _events = [];
        private readonly HashSet<string> _faultReasons = new(StringComparer.Ordinal);
        private bool _incomplete;
        private bool _traceTruncated;
        internal int RulesEvaluated { get; set; }
        internal int MatchAttempts { get; set; }
        internal int RegexCacheHits { get; set; }
        internal int FaultCount { get; private set; }
        internal string? LastIssue { get; private set; }

        internal Run(CancellationToken ct) { _ct = ct; _prior = Active.Value; Active.Value = this; }
        internal bool CanContinue()
        {
            _ct.ThrowIfCancellationRequested();
            if (_clock.ElapsedMilliseconds <= Budget) return true;
            Incomplete("RegexBudgetExceeded");
            return false;
        }

        internal void Trace(RuleTraceEvent trace)
        {
            if (_events.Count < TraceLimit) _events.Add(trace);
            else _traceTruncated = true;
        }

        internal void Incomplete(string reason)
        {
            _incomplete = true;
            FaultCount++;
            LastIssue = reason;
            if (_faultReasons.Count < 16) _faultReasons.Add(reason);
            if (!_events.Any(item => item.Reason == reason)) Trace(new("engine", "incomplete", reason));
        }

        internal RuleParseResult Complete(RuleParseResult result)
        {
            CanContinue();
            RuleExecutionDiagnostics diagnostics = new(_incomplete ? "incomplete" : "complete", RulesEvaluated,
                MatchAttempts, RegexCacheHits, _clock.Elapsed.TotalMilliseconds, Budget, _traceTruncated, _events.ToArray(),
                _faultReasons.Order(StringComparer.Ordinal).ToArray());
            return result with
            {
                Diagnostics = diagnostics,
                Confidence = _incomplete ? Math.Min(result.Confidence, 0.49) : result.Confidence,
                HasIdentityEvidence = !_incomplete && result.HasIdentityEvidence,
                Episode = _incomplete ? null : result.Episode,
                EpisodeEnd = _incomplete ? null : result.EpisodeEnd,
                RejectedFields = _incomplete
                    ? (result.RejectedFields ?? []).Concat(["episode", "episodeEnd"]).Distinct().ToArray() : result.RejectedFields,
                Conflicts = _incomplete ? (result.Conflicts ?? []).Append("executionIncomplete").Distinct().ToArray() : result.Conflicts,
            };
        }

        public void Dispose() => Active.Value = _prior;
    }
}
