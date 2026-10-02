using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;
using PersonalMediaManager.Application.Services.Tmdb;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>有容量和时效边界的建议缓存</summary>
public sealed class LocalMediaSuggestionCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, LocalMediaAssistResult Result)> _items = new();
    internal LocalMediaAssistResult? Get(string key, DateTimeOffset now) =>
        _items.TryGetValue(key, out (DateTimeOffset Expires, LocalMediaAssistResult Result) value)
        && value.Expires > now ? value.Result with { FromCache = true, InferenceAttempted = false } : null;
    internal void Put(string key, DateTimeOffset now, LocalMediaAssistResult result)
    {
        if (_items.Count >= 128) _items.Clear();
        _items[key] = (now.AddMinutes(10), result);
    }
}

/// <summary>保留原始证据的候选生成与有限检索</summary>
public sealed class LocalMediaAssistService(ILocalAiSettingsService settings, ILocalAiInferenceClient client,
    ITmdbSearchService tmdb, LocalMediaSuggestionCache cache, IClock clock) : ILocalMediaAssistService
{
    private const int MaxCandidates = 1;
    private const string PromptVersion = LocalTitleSpanProtocol.Version;

    public async Task<LocalMediaAssistResult> SuggestAsync(FileParseContext source, RuleParseResult? rule,
        LocalAiMode expectedMode, CancellationToken ct = default)
    {
        using IDisposable? diagnosticScope = ParseDiagnostics.CurrentRunId is null ? ParseDiagnostics.Begin("local_ai_assist") : null;
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            LocalMediaAssistResult result = await SuggestCoreAsync(source, expectedMode, ct);
            ParseDiagnostics.Emit("local_ai.assist_result", new
            {
                Mode = result.Mode.ToString(), result.Status, result.Reasons,
                Model = ParseDiagnostics.CaptureText(result.ModelId, includeAtStandard: true), result.FromCache,
                result.InferenceAttempted, result.ElapsedMilliseconds, TotalElapsedMs = elapsed.ElapsedMilliseconds,
                result.ProtocolVersion, CandidateCount = result.Candidates.Count,
                OfferedCount = result.OfferedCandidates?.Count,
                Candidates = ParseDiagnostics.CaptureText(JsonSerializer.Serialize(result.Candidates)),
            });
            return result;
        }
        catch (Exception ex)
        {
            ParseDiagnostics.Emit(ex is OperationCanceledException ? "local_ai.assist_cancelled" : "local_ai.assist_failed", new
            {
                Mode = expectedMode.ToString(), ElapsedMs = elapsed.ElapsedMilliseconds,
                ExceptionType = ex.GetType().Name, CallerCancelled = ct.IsCancellationRequested,
            });
            throw;
        }
    }

    private async Task<LocalMediaAssistResult> SuggestCoreAsync(FileParseContext source,
        LocalAiMode expectedMode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LocalAiSettingsDto configuration;
        try { configuration = await settings.GetAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ParseDiagnostics.Emit("local_ai.settings_failed", new { ExceptionType = ex.GetType().Name });
            return new(expectedMode, "Unavailable", [], ["SettingsUnavailable"]);
        }
        if (configuration.Mode == LocalAiMode.Disabled || configuration.Mode != expectedMode)
            return new(configuration.Mode, "NotScheduled", [], []);

        string fileName = source.FileName;
        string? parent = source.DirectParentFolderName;
        int? parentIndex = parent is null ? null : source.RelativeSegments.Count - 1;
        // 超长输入或候选溢出直接弃权，不能静默截断原文再声称已核验来源。
        if (fileName.Length > 512 || parent?.Length > 256)
            return new(expectedMode, "Rejected", [], ["SourceTooLong"], configuration.ModelId,
                ProtocolVersion: PromptVersion);
        LocalSourceSpanPool pool = LocalTitleSpanProtocol.Build(fileName, parent);
        if (pool.UnsupportedUnicode || pool.Truncated || pool.Rows.Count == 0)
            return new(expectedMode, "Rejected", [], [pool.UnsupportedUnicode ? "UnsupportedUnicodeCategory"
                : pool.Truncated ? "CandidatePoolTruncated" : "NoSourceCandidates"],
                configuration.ModelId, ProtocolVersion: PromptVersion);
        // 与冻结协议相同：仅文件名、直接父目录和程序生成的原文区间，不传规则 hint 或绝对路径。
        string user = LocalTitleSpanProtocol.UserPrompt(fileName, parent, pool.Rows);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            PromptVersion + JsonSerializer.Serialize(configuration) + parentIndex + ":" + user)));
        LocalMediaAssistResult? cached = cache.Get(key, clock.UtcNow);
        DiagnosticText systemText = ParseDiagnostics.CaptureText(LocalTitleSpanProtocol.SystemPrompt);
        DiagnosticText userText = ParseDiagnostics.CaptureText(user);
        DiagnosticText modelText = ParseDiagnostics.CaptureText(configuration.ModelId, includeAtStandard: true);
        // 缓存键含原始输入与运行时路径，只在内存使用；诊断指纹只组合脱敏内容的哈希和安全参数。
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ProtocolVersion = PromptVersion, ModelSha256 = modelText.Sha256,
            SystemSha256 = systemText.Sha256, UserSha256 = userText.Sha256,
            configuration.Threads, configuration.ContextTokens, configuration.MaxOutputTokens,
            configuration.TimeoutSeconds, RequestedMaxTokens = 32, AllowedSpanCount = pool.Rows.Count,
        }))));
        ParseDiagnostics.Emit("local_ai.request", new
        {
            ProtocolVersion = PromptVersion, Fingerprint = fingerprint, CacheHit = cached is not null,
            Model = modelText, System = systemText, User = userText, RequestedMaxTokens = 32,
            AllowedSpanCount = pool.Rows.Count,
        });
        if (cached is not null) return cached;
        LocalAiInferenceResult inference;
        try { inference = await client.GenerateAsync(new(LocalTitleSpanProtocol.SystemPrompt, user, 32, pool.Rows.Count), ct).WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ParseDiagnostics.Emit("local_ai.inference_failed", new { ExceptionType = ex.GetType().Name });
            return new(expectedMode, "Unavailable", [], ["InferenceUnavailable"], configuration.ModelId);
        }
        ct.ThrowIfCancellationRequested();
        ParseDiagnostics.Emit("local_ai.response", new
        {
            Fingerprint = fingerprint, inference.Success, inference.Attempted, inference.ElapsedMilliseconds,
            inference.FinishReason, inference.FailureReason,
            Model = ParseDiagnostics.CaptureText(inference.ModelId, includeAtStandard: true),
            Response = inference.Content is null && inference.FailureReason != "empty_response"
                ? ParseDiagnostics.UnknownText() : ParseDiagnostics.CaptureText(inference.Content),
        });
        if (!inference.Success || inference.Content is null || inference.FinishReason == "length"
            || (inference.ModelId is not null && inference.ModelId != configuration.ModelId))
            return new(expectedMode, "Unavailable", [], [inference.FinishReason == "length" ? "Truncated" : inference.FailureReason ?? "NoResponseOrModelChanged"],
                configuration.ModelId, InferenceAttempted: inference.Attempted, ElapsedMilliseconds: inference.ElapsedMilliseconds);
        LocalMediaAssistResult result = Parse(inference.Content, fileName, parent, parentIndex, expectedMode, configuration.ModelId, pool)
            with { InferenceAttempted = inference.Attempted, ElapsedMilliseconds = inference.ElapsedMilliseconds };
        if (result.Status == "Validated") cache.Put(key, clock.UtcNow, result);
        return result;
    }

    internal static LocalMediaAssistResult Parse(string content, string fileName, string? parent,
        int? parentIndex, LocalAiMode mode, string modelId, LocalSourceSpanPool? offered = null)
    {
        LocalSourceSpanPool pool = offered ?? LocalTitleSpanProtocol.Build(fileName, parent);
        LocalMediaSuggestion[] rows = pool.Rows.Select(row => Suggestion(row, parentIndex)).ToArray();
        LocalMediaAssistResult Result(string status, IReadOnlyList<LocalMediaSuggestion> selected, string reason) =>
            new(mode, status, selected, [reason], modelId, content, OfferedCandidates: rows, ProtocolVersion: PromptVersion);
        if (pool.UnsupportedUnicode) return Result("Rejected", [], "UnsupportedUnicodeCategory");
        if (pool.Truncated) return Result("Rejected", [], "CandidatePoolTruncated");
        if (Encoding.UTF8.GetByteCount(content) > 16384) return Result("Rejected", [], "ResponseTooLarge");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content, new() { MaxDepth = 8 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result("Rejected", [], "InvalidIndexShape");
            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                return Result("Rejected", [], "DuplicateProperty");
            if (properties.Length != 1 || properties[0].Name != "index")
                return Result("Rejected", [], "InvalidIndexShape");
            JsonElement value = properties[0].Value;
            if (value.ValueKind == JsonValueKind.Null) return Result("Validated", [], "Abstain");
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int index)
                || index < 0 || index >= pool.Rows.Count) return Result("Rejected", [], "IndexOutsideClosedSet");
            LocalSourceSpan row = pool.Rows[index];
            string? raw = row.Source == "fileName" ? fileName : parent;
            if (raw is null || row.Index != index || row.Start < 0 || row.End > raw.Length
                || row.End <= row.Start || raw[row.Start..row.End] != row.Text)
                return Result("Rejected", [], "SourceSpanMismatch");
            if (LocalTitleSpanProtocol.IsObviousNoise(row)) return Result("Validated", [], "ObviousSourceNoise");
            // 模型没有自由标题字段；查询逐字来自闭集原文，字面正确仍不等于作品身份正确。
            return Result("Validated", [rows[index]], "LiteralSpanOnlyNotIdentityVerification");
        }
        catch (JsonException) { return Result("Rejected", [], "InvalidJson"); }
        catch (ArgumentException) { return Result("Rejected", [], "InvalidText"); }
    }

    private static LocalMediaSuggestion Suggestion(LocalSourceSpan row, int? parentIndex)
    {
        string source = row.Source == "fileName" ? "FileName" : "RelativeSegment";
        int? segment = row.Source == "fileName" ? null : parentIndex;
        string group = $"{source}:{segment}:{row.Start}:{row.End - row.Start}";
        return new(row.Text, row.Text, source, segment, row.Start, row.End - row.Start, group, "LiteralTitle");
    }

    public async Task<LocalMediaLookupResult> LookupAsync(LocalMediaAssistResult suggestions,
        IReadOnlyList<TmdbSearchRequest> alreadyQueried, string language, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (suggestions.Status != "Validated") return new([], [], false, true);
        List<TmdbCandidate> candidates = [];
        List<LocalMediaQuery> queries = [];
        HashSet<string> seenTitles = new(StringComparer.Ordinal);
        HashSet<string> seenQueries = alreadyQueried.Select(QueryKey).ToHashSet(StringComparer.Ordinal);
        HashSet<(int, string)> identities = [];
        bool allCached = true;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        foreach (LocalMediaSuggestion suggestion in suggestions.Candidates.Take(MaxCandidates))
        {
            if (!seenTitles.Add(Key(suggestion.Title))) continue;
            foreach (string type in new[] { "tv", "movie" })
            {
                TmdbSearchRequest query = new(suggestion.Title, type, null, language, language);
                if (!seenQueries.Add(QueryKey(query))) continue;
                try
                {
                    // 年份不设过滤，语言相同，禁用既有搜索器的年/语种回退扩张；一个原文候选至多两次逻辑搜索。
                    TmdbSearchResult found = await tmdb.SearchAsync(query, deadline.Token).WaitAsync(deadline.Token);
                    allCached &= found.FromCache;
                    queries.Add(new(suggestion.Title, type, found.FromCache ? "Cache" : "Remote", found.Candidates.Count));
                    foreach (TmdbCandidate candidate in found.Candidates.Take(50))
                        if (candidate.Id > 0 && candidate.MediaType is "tv" or "movie"
                            && identities.Add((candidate.Id, candidate.MediaType))) candidates.Add(candidate);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                { queries.Add(new(suggestion.Title, type, "Deadline", 0)); return new(candidates, queries, true, allCached); }
                catch (Exception)
                {
                    // 鉴权、网络和缓存故障不伪装成零结果，也不换词绕过失败重试。
                    queries.Add(new(suggestion.Title, type, "Failed", 0)); return new(candidates, queries, true, allCached);
                }
            }
        }
        return new(candidates, queries, false, allCached);
    }

    internal static string Key(string value) => value.Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();

    private static string QueryKey(TmdbSearchRequest query) => JsonSerializer.Serialize(new
        { title = Key(query.Query), type = query.MediaType.ToLowerInvariant(), query.Year, query.Language, query.FallbackLanguage });
}
