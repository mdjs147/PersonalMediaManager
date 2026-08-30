using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Tmdb;

/// <summary>TMDB v3 客户端实现（D2.1）</summary>
/// <remarks>
/// - 节流：内置令牌桶（默认 40 token/s），所有请求先 await ConsumeAsync 再发出。
///   速率可由调用方按请求传入（来源 Tmdb_Setting.RateLimitPerSecond，修复「死旋钮」：旧版硬编码 40）：
///   传入值与当前不同则原地换桶（粘性保持到下次变更），传 null 沿用当前；调用方（TmdbSearchService）
///   每次本就要读 Tmdb_Setting 拿 ApiKey，顺带传速率零额外查库开销，无需再做 TTL 缓存。
/// - 429 退避：按 Retry-After 头退避，最多重试 3 次；等待超过 MaxRetryAfterSeconds（60s）视为不可恢复
///   直接抛 TmdbClientException（防止单文件把串行管线堵数小时——AI 链有 120s 顶层超时而 TMDB 原本没有）。
/// - 鉴权：v4 Bearer Token（Authorization: Bearer {ApiKey}）。
/// - 语言：Search 用 language 参数（请求未指定时按 zh-CN 兜底）；TV/Movie 详情同样。
/// 不依赖 Polly 大套件，节流 + 重试用极简实现，避免 Polly v8/v7 API 漂移与配置复杂度。
/// </remarks>
internal sealed class TmdbClient : ITmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxRetries = 3;
    public const int DefaultRequestsPerSecond = 40;

    /// <summary>429 Retry-After 可接受等待上限（秒）；超过即判定不可恢复，抛异常交由重扫重试</summary>
    public const int MaxRetryAfterSeconds = 60;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TmdbClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly object _rateGate = new();

    // volatile：换桶在锁内，消费端无锁读最新引用即可（PMM 管线本身串行，竞争极低）
    private volatile TokenBucketRateLimiter _rateLimiter;

    // 当前生效速率（req/s）；0 = 测试注入自定义限流器、速率未知（首个显式速率会触发换桶）
    private int _currentRate;

    public TmdbClient(IHttpClientFactory httpFactory, ILogger<TmdbClient> logger)
        : this(httpFactory, logger, new TokenBucketRateLimiter(DefaultRequestsPerSecond, TimeSpan.FromSeconds(1)))
    {
        _currentRate = DefaultRequestsPerSecond;
    }

    /// <summary>测试用构造：允许注入自定义速率限制器与退避等待委托以观察节流/退避行为</summary>
    internal TmdbClient(
        IHttpClientFactory httpFactory,
        ILogger<TmdbClient> logger,
        TokenBucketRateLimiter rateLimiter,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>当前生效限流速率（req/s，测试观察用；0=注入限流器速率未知）</summary>
    internal int CurrentRateLimitPerSecond => _currentRate;

    /// <summary>当前限流器实例（测试观察换桶/复用用）</summary>
    internal TokenBucketRateLimiter CurrentRateLimiter => _rateLimiter;

    /// <summary>按调用方传入的速率惰性换桶（null / 非正数 = 沿用当前；速率相同不换避免清空已积累令牌）</summary>
    private void ApplyRateLimit(int? requestedPerSecond)
    {
        if (requestedPerSecond is not int rate || rate <= 0) return;
        lock (_rateGate)
        {
            if (rate == _currentRate) return;
            _rateLimiter = new TokenBucketRateLimiter(rate, TimeSpan.FromSeconds(1));
            _logger.LogInformation("TMDB 限流速率已更新：{Old} → {New} 请求/秒", _currentRate, rate);
            _currentRate = rate;
        }
    }

    public async Task<TmdbSearchResult> SearchAsync(TmdbSearchRequest request, string apiKey, int? rateLimitPerSecond = null, CancellationToken ct = default)
    {
        if (request is null) throw new TmdbClientException("Request 不能为空");
        if (string.IsNullOrWhiteSpace(request.Query)) throw new TmdbClientException("Query 不能为空");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TmdbClientException("ApiKey 不能为空");
        ApplyRateLimit(rateLimitPerSecond);

        // unknown 类型 → 并发两个查询合并（按顺序，避免破坏节流）
        if (string.Equals(request.MediaType, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            TmdbSearchResult movie = await DoSearchAsync(request with { MediaType = "movie" }, apiKey, ct);
            TmdbSearchResult tv = await DoSearchAsync(request with { MediaType = "tv" }, apiKey, ct);
            List<TmdbCandidate> merged = [.. movie.Candidates, .. tv.Candidates];
            string? raw = $"{{\"movie\":{movie.RawJson ?? "null"},\"tv\":{tv.RawJson ?? "null"}}}";
            return new TmdbSearchResult(merged, raw);
        }

        return await DoSearchAsync(request, apiKey, ct);
    }

    private async Task<TmdbSearchResult> DoSearchAsync(TmdbSearchRequest request, string apiKey, CancellationToken ct)
    {
        string endpoint = request.MediaType.ToLowerInvariant() switch
        {
            "movie" => "/search/movie",
            "tv" => "/search/tv",
            _ => throw new TmdbClientException($"不支持的 MediaType：{request.MediaType}（仅 movie / tv / unknown）"),
        };

        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["query"] = request.Query,
            // Language=null 表示调用方未显式指定（编排层正常会注入 Tmdb_Setting）；直连本客户端时按 zh-CN 兜底，与旧版 record 默认一致
            ["language"] = request.Language ?? "zh-CN",
            ["include_adult"] = "false",
        };
        if (request.Year is not null)
        {
            // movie 端点用 year；tv 端点用 first_air_date_year
            string yearKey = request.MediaType.Equals("movie", StringComparison.OrdinalIgnoreCase)
                ? "year" : "first_air_date_year";
            query[yearKey] = request.Year.Value.ToString();
        }

        HttpResponseMessage resp = await SendWithRetryAsync(HttpMethod.Get, endpoint, query, apiKey, content: null, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);

        List<TmdbCandidate> candidates;
        try
        {
            candidates = ParseSearchResponse(body, request.MediaType);
        }
        catch (JsonException ex)
        {
            throw new TmdbClientException($"TMDB 搜索响应 JSON 解析失败：{ex.Message}", (int)resp.StatusCode, ex);
        }
        return new TmdbSearchResult(candidates, body);
    }

    public async Task<TmdbDetailsResult> GetDetailsAsync(int tmdbId, string mediaType, string apiKey, string language = "zh-CN", int? rateLimitPerSecond = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TmdbClientException("ApiKey 不能为空");
        ApplyRateLimit(rateLimitPerSecond);
        string endpoint = mediaType.ToLowerInvariant() switch
        {
            "movie" => $"/movie/{tmdbId}",
            "tv" => $"/tv/{tmdbId}",
            _ => throw new TmdbClientException($"不支持的 MediaType：{mediaType}"),
        };
        Dictionary<string, string?> query = new(StringComparer.Ordinal) { ["language"] = language };

        HttpResponseMessage resp = await SendWithRetryAsync(HttpMethod.Get, endpoint, query, apiKey, content: null, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return ParseDetailsResponse(body, tmdbId, mediaType);
        }
        catch (JsonException ex)
        {
            throw new TmdbClientException($"TMDB 详情响应 JSON 解析失败：{ex.Message}", (int)resp.StatusCode, ex);
        }
    }

    public async Task<TmdbEnrichedDetails> GetEnrichedDetailsAsync(int tmdbId, string mediaType, string apiKey, string language = "zh-CN", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TmdbClientException("ApiKey 不能为空");
        string normType = mediaType.ToLowerInvariant();
        string endpoint = normType switch
        {
            "movie" => $"/movie/{tmdbId}",
            "tv" => $"/tv/{tmdbId}",
            _ => throw new TmdbClientException($"不支持的 MediaType：{mediaType}"),
        };
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["language"] = language,
            ["append_to_response"] = "credits,keywords",
        };

        HttpResponseMessage resp = await SendWithRetryAsync(HttpMethod.Get, endpoint, query, apiKey, content: null, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return ParseEnrichedResponse(body, tmdbId, normType);
        }
        catch (JsonException ex)
        {
            throw new TmdbClientException($"TMDB 富化详情 JSON 解析失败：{ex.Message}", (int)resp.StatusCode, ex);
        }
    }

    public async Task<TmdbSeasonDetail> GetSeasonAsync(int tmdbId, int seasonNumber, string apiKey, string language = "zh-CN", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TmdbClientException("ApiKey 不能为空");
        string endpoint = $"/tv/{tmdbId}/season/{seasonNumber}";
        Dictionary<string, string?> query = new(StringComparer.Ordinal) { ["language"] = language };

        HttpResponseMessage resp = await SendWithRetryAsync(HttpMethod.Get, endpoint, query, apiKey, content: null, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return ParseSeasonResponse(body, seasonNumber);
        }
        catch (JsonException ex)
        {
            throw new TmdbClientException($"TMDB 季详情 JSON 解析失败：{ex.Message}", (int)resp.StatusCode, ex);
        }
    }

    public async Task<TmdbEpisodeGroup> GetEpisodeGroupAsync(string episodeGroupId, string apiKey, string language = "zh-CN", int? rateLimitPerSecond = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episodeGroupId)) throw new TmdbClientException("EpisodeGroupId 不能为空");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new TmdbClientException("ApiKey 不能为空");
        ApplyRateLimit(rateLimitPerSecond);
        string endpoint = $"/tv/episode_group/{episodeGroupId}";
        Dictionary<string, string?> query = new(StringComparer.Ordinal) { ["language"] = language };

        HttpResponseMessage resp = await SendWithRetryAsync(HttpMethod.Get, endpoint, query, apiKey, content: null, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return ParseEpisodeGroupResponse(body, episodeGroupId);
        }
        catch (JsonException ex)
        {
            throw new TmdbClientException($"TMDB 剧集组 JSON 解析失败：{ex.Message}", (int)resp.StatusCode, ex);
        }
    }

    /// <summary>剧组保留的关键职务（避免存入上百条无关 crew）</summary>
    private static readonly HashSet<string> CrewJobWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Director", "Writer", "Screenplay", "Story", "Creator", "Producer", "Executive Producer",
        "Co-Executive Producer", "Original Music Composer", "Director of Photography", "Editor", "Novel", "Author",
    };

    private const int MaxCastCount = 30;

    private static TmdbEnrichedDetails ParseEnrichedResponse(string body, int tmdbId, string mediaType)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool isTv = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);

        string? title = isTv ? GetStr(root, "name") : GetStr(root, "title");
        string? originalTitle = isTv ? GetStr(root, "original_name") : GetStr(root, "original_title");
        string dateField = isTv ? "first_air_date" : "release_date";
        int? year = ExtractYear(root, dateField);
        DateTimeOffset? releaseDate = ExtractDate(root, dateField);
        int? runtime = isTv
            ? (root.TryGetProperty("episode_run_time", out JsonElement ert) && ert.ValueKind == JsonValueKind.Array
                && ert.GetArrayLength() > 0 && ert[0].TryGetInt32(out int rt) ? rt : null)
            : GetInt(root, "runtime");

        List<TmdbGenreRef> genres = ParseRefList(root, "genres", e =>
            new TmdbGenreRef(GetInt(e, "id") ?? 0, GetStr(e, "name") ?? string.Empty));
        List<TmdbCompanyRef> companies = ParseRefList(root, "production_companies", e =>
            new TmdbCompanyRef(GetInt(e, "id") ?? 0, GetStr(e, "name") ?? string.Empty, GetStr(e, "logo_path"), GetStr(e, "origin_country")));
        List<TmdbNetworkRef> networks = isTv
            ? ParseRefList(root, "networks", e =>
                new TmdbNetworkRef(GetInt(e, "id") ?? 0, GetStr(e, "name") ?? string.Empty, GetStr(e, "logo_path"), GetStr(e, "origin_country")))
            : new List<TmdbNetworkRef>();
        List<TmdbKeywordRef> keywords = ParseKeywords(root, isTv);
        (List<TmdbCreditRef> cast, List<TmdbCreditRef> crew) = ParseCredits(root, isTv);
        List<TmdbSeasonSummary> seasons = isTv ? ParseSeasonSummaries(root) : new List<TmdbSeasonSummary>();

        return new TmdbEnrichedDetails(
            tmdbId,
            mediaType.ToLowerInvariant(),
            title,
            originalTitle,
            year,
            GetStr(root, "overview"),
            GetStr(root, "tagline"),
            GetStr(root, "poster_path"),
            GetStr(root, "backdrop_path"),
            runtime,
            GetDouble(root, "vote_average"),
            GetInt(root, "vote_count"),
            releaseDate,
            GetStr(root, "status"),
            GetStr(root, "original_language"),
            ParseStringArray(root, "origin_country"),
            GetStr(root, "homepage"),
            GetInt(root, "number_of_seasons"),
            GetInt(root, "number_of_episodes"),
            genres,
            companies,
            networks,
            keywords,
            cast,
            crew,
            seasons,
            body);
    }

    private static List<TmdbKeywordRef> ParseKeywords(JsonElement root, bool isTv)
    {
        if (!root.TryGetProperty("keywords", out JsonElement kwObj) || kwObj.ValueKind != JsonValueKind.Object)
            return new List<TmdbKeywordRef>();
        string innerField = isTv ? "results" : "keywords";
        if (!kwObj.TryGetProperty(innerField, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<TmdbKeywordRef>();
        List<TmdbKeywordRef> list = new(arr.GetArrayLength());
        foreach (JsonElement e in arr.EnumerateArray())
        {
            int id = GetInt(e, "id") ?? 0;
            string? name = GetStr(e, "name");
            if (id != 0 && !string.IsNullOrWhiteSpace(name)) list.Add(new TmdbKeywordRef(id, name));
        }
        return list;
    }

    private static (List<TmdbCreditRef> Cast, List<TmdbCreditRef> Crew) ParseCredits(JsonElement root, bool isTv)
    {
        List<TmdbCreditRef> cast = new();
        List<TmdbCreditRef> crew = new();
        if (root.TryGetProperty("credits", out JsonElement credits) && credits.ValueKind == JsonValueKind.Object)
        {
            if (credits.TryGetProperty("cast", out JsonElement castArr) && castArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in castArr.EnumerateArray())
                {
                    int id = GetInt(c, "id") ?? 0;
                    string? name = GetStr(c, "name");
                    if (id == 0 || string.IsNullOrWhiteSpace(name)) continue;
                    cast.Add(new TmdbCreditRef(id, name, GetStr(c, "profile_path"), GetStr(c, "known_for_department"),
                        GetStr(c, "character"), GetInt(c, "order"), null, null));
                }
            }
            if (credits.TryGetProperty("crew", out JsonElement crewArr) && crewArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in crewArr.EnumerateArray())
                {
                    string? job = GetStr(c, "job");
                    if (job is null || !CrewJobWhitelist.Contains(job)) continue;
                    int id = GetInt(c, "id") ?? 0;
                    string? name = GetStr(c, "name");
                    if (id == 0 || string.IsNullOrWhiteSpace(name)) continue;
                    crew.Add(new TmdbCreditRef(id, name, GetStr(c, "profile_path"), GetStr(c, "known_for_department"),
                        null, null, job, GetStr(c, "department")));
                }
            }
        }
        // 剧集 created_by 视为 Creator（TMDB 把主创单列，不在 credits.crew 内）
        if (isTv && root.TryGetProperty("created_by", out JsonElement creators) && creators.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in creators.EnumerateArray())
            {
                int id = GetInt(c, "id") ?? 0;
                string? name = GetStr(c, "name");
                if (id == 0 || string.IsNullOrWhiteSpace(name)) continue;
                if (crew.Any(x => x.PersonId == id && string.Equals(x.Job, "Creator", StringComparison.OrdinalIgnoreCase))) continue;
                crew.Add(new TmdbCreditRef(id, name, GetStr(c, "profile_path"), null, null, null, "Creator", "Created By"));
            }
        }

        // 演员按 order 截断到前 N，去重保号
        List<TmdbCreditRef> topCast = cast
            .OrderBy(x => x.Order ?? int.MaxValue)
            .Take(MaxCastCount)
            .ToList();
        return (topCast, crew);
    }

    private static List<TmdbSeasonSummary> ParseSeasonSummaries(JsonElement root)
    {
        if (!root.TryGetProperty("seasons", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<TmdbSeasonSummary>();
        List<TmdbSeasonSummary> list = new(arr.GetArrayLength());
        foreach (JsonElement s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            int? sn = GetInt(s, "season_number");
            if (sn is null) continue;
            list.Add(new TmdbSeasonSummary(
                sn.Value,
                GetStr(s, "name"),
                GetStr(s, "overview"),
                GetStr(s, "poster_path"),
                ExtractDate(s, "air_date"),
                GetInt(s, "episode_count") ?? 0));
        }
        return list;
    }

    private static TmdbSeasonDetail ParseSeasonResponse(string body, int seasonNumber)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        List<TmdbEpisodeRef> episodes = new();
        if (root.TryGetProperty("episodes", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in arr.EnumerateArray())
            {
                int? en = GetInt(e, "episode_number");
                if (en is null) continue;
                episodes.Add(new TmdbEpisodeRef(
                    en.Value,
                    GetStr(e, "name"),
                    GetStr(e, "overview"),
                    GetStr(e, "still_path"),
                    ExtractDate(e, "air_date"),
                    GetInt(e, "runtime"),
                    GetDouble(e, "vote_average")));
            }
        }
        return new TmdbSeasonDetail(
            seasonNumber,
            GetStr(root, "name"),
            GetStr(root, "overview"),
            GetStr(root, "poster_path"),
            ExtractDate(root, "air_date"),
            episodes);
    }

    private static TmdbEpisodeGroup ParseEpisodeGroupResponse(string body, string episodeGroupId)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        List<TmdbEpisodeGroupSegment> segments = new();
        if (root.TryGetProperty("groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement g in groups.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                List<TmdbEpisodeGroupEntry> entries = new();
                if (g.TryGetProperty("episodes", out JsonElement epArr) && epArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement e in epArr.EnumerateArray())
                    {
                        int? sn = GetInt(e, "season_number");
                        int? en = GetInt(e, "episode_number");
                        if (sn is null || en is null) continue;
                        // 编组内 order（0 起）缺失时回退为当前累计序，保持稳定的编组内位置
                        int order = GetInt(e, "order") ?? entries.Count;
                        entries.Add(new TmdbEpisodeGroupEntry(order, sn.Value, en.Value, GetStr(e, "name"), GetInt(e, "id")));
                    }
                }
                segments.Add(new TmdbEpisodeGroupSegment(
                    GetStr(g, "id") ?? string.Empty,
                    GetStr(g, "name"),
                    GetInt(g, "order") ?? 0,
                    entries));
            }
        }
        return new TmdbEpisodeGroup(
            GetStr(root, "id") ?? episodeGroupId,
            GetStr(root, "name"),
            GetInt(root, "type") ?? 0,
            segments);
    }

    /// <summary>通用对象数组映射（genres/production_companies/networks 等）</summary>
    private static List<T> ParseRefList<T>(JsonElement root, string field, Func<JsonElement, T> map)
    {
        if (!root.TryGetProperty(field, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<T>();
        List<T> list = new(arr.GetArrayLength());
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object) list.Add(map(e));
        }
        return list;
    }

    private static List<string>? ParseStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return null;
        List<string> list = arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    private static double? GetDouble(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static DateTimeOffset? ExtractDate(JsonElement el, string field)
    {
        string? raw = GetStr(el, field);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d) ? d : null;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method, string endpoint, IReadOnlyDictionary<string, string?> query,
        string apiKey, HttpContent? content, CancellationToken ct)
    {
        HttpClient client = _httpFactory.CreateClient("TmdbClient");
        if (client.Timeout == TimeSpan.FromSeconds(100)) // 默认值，未被外部设置过
            client.Timeout = TimeSpan.FromSeconds(30);

        string url = BaseUrl + endpoint + BuildQueryString(query);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await _rateLimiter.ConsumeAsync(ct);

            using HttpRequestMessage req = new(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (content is not null) req.Content = content;

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new TmdbClientException($"TMDB 请求 HTTP 异常：{ex.Message}", inner: ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TmdbClientException("TMDB 请求超时", inner: ex);
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt >= MaxRetries)
                {
                    resp.Dispose();
                    throw new TmdbClientException($"TMDB 429 退避 {MaxRetries} 次后仍未恢复", (int)HttpStatusCode.TooManyRequests);
                }
                TimeSpan delay = ParseRetryAfter(resp.Headers.RetryAfter) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                resp.Dispose();
                // Retry-After 上限钳制：原样信任服务端（可能给 3600s）会让单文件堵死串行管线数小时，
                // 超过 60s 视为不可恢复 → 直接抛异常结束本次处理，交由后续重扫重试
                if (delay > TimeSpan.FromSeconds(MaxRetryAfterSeconds))
                {
                    throw new TmdbClientException(
                        $"TMDB 限流等待过长({(int)delay.TotalSeconds}s)，稍后将由重扫重试",
                        (int)HttpStatusCode.TooManyRequests);
                }
                _logger.LogWarning("TMDB 429，第 {Attempt} 次退避 {Delay}", attempt + 1, delay);
                await _delayAsync(delay, ct);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                int status = (int)resp.StatusCode;
                string body = await resp.Content.ReadAsStringAsync(ct);
                resp.Dispose();
                throw new TmdbClientException($"TMDB {status}：{body}", status);
            }

            return resp;
        }

        throw new TmdbClientException($"TMDB 请求超出重试次数 {MaxRetries}");
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta.HasValue) return header.Delta.Value;
        if (header.Date.HasValue)
        {
            TimeSpan diff = header.Date.Value - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> query)
    {
        if (query.Count == 0) return string.Empty;
        List<string> parts = [];
        foreach (KeyValuePair<string, string?> kv in query)
        {
            if (kv.Value is null) continue;
            parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        }
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static List<TmdbCandidate> ParseSearchResponse(string body, string mediaType)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
            return [];

        List<TmdbCandidate> list = new(results.GetArrayLength());
        bool isTv = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);
        foreach (JsonElement item in results.EnumerateArray())
        {
            int id = item.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt32() : 0;
            string? title = isTv
                ? GetStr(item, "name") ?? GetStr(item, "original_name")
                : GetStr(item, "title") ?? GetStr(item, "original_title");
            string? originalTitle = isTv ? GetStr(item, "original_name") : GetStr(item, "original_title");
            int? year = ExtractYear(item, isTv ? "first_air_date" : "release_date");
            double popularity = item.TryGetProperty("popularity", out JsonElement popEl) && popEl.TryGetDouble(out double p) ? p : 0;
            string? originalLang = GetStr(item, "original_language");
            List<string>? originCountry = null;
            if (item.TryGetProperty("origin_country", out JsonElement ocEl) && ocEl.ValueKind == JsonValueKind.Array)
            {
                originCountry = ocEl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
            string? poster = GetStr(item, "poster_path");
            string? overview = GetStr(item, "overview");
            list.Add(new TmdbCandidate(id, mediaType.ToLowerInvariant(), title, originalTitle, year, popularity, originalLang, originCountry, poster, overview));
        }
        return list;
    }

    private static TmdbDetailsResult ParseDetailsResponse(string body, int tmdbId, string mediaType)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool isTv = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase);
        string? title = isTv ? GetStr(root, "name") : GetStr(root, "title");
        string? originalTitle = isTv ? GetStr(root, "original_name") : GetStr(root, "original_title");
        int? year = ExtractYear(root, isTv ? "first_air_date" : "release_date");
        int? totalSeasons = isTv && root.TryGetProperty("number_of_seasons", out JsonElement nsEl) && nsEl.TryGetInt32(out int ns) ? ns : null;
        string? poster = GetStr(root, "poster_path");
        string? originalLanguage = GetStr(root, "original_language");
        List<string>? originCountry = null;
        if (root.TryGetProperty("origin_country", out JsonElement ocEl) && ocEl.ValueKind == JsonValueKind.Array)
        {
            originCountry = ocEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }
        string? genresJson = root.TryGetProperty("genres", out JsonElement genresEl) ? genresEl.GetRawText() : null;
        string? overview = GetStr(root, "overview");
        // 剧集逐季集数（复用已 Parse 的 root，避免二次解析）；电影无 seasons
        IReadOnlyList<TmdbSeasonInfo>? seasons = isTv ? TmdbSeasonsParser.Parse(root) : null;
        return new TmdbDetailsResult(tmdbId, mediaType.ToLowerInvariant(), title, originalTitle, year, totalSeasons, poster, originCountry, originalLanguage, genresJson, overview, body, seasons);
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        return v.GetString();
    }

    private static int? ExtractYear(JsonElement el, string dateField)
    {
        string? raw = GetStr(el, dateField);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Length >= 4 && int.TryParse(raw.AsSpan(0, 4), out int y) ? y : null;
    }
}
