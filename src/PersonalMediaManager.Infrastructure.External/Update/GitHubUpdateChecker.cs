using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;

namespace PersonalMediaManager.Infrastructure.External.Update;

/// <summary>IUpdateChecker 实现：调 GitHub /releases/latest 取最新版本（D9.1 客户端检查更新）</summary>
/// <remarks>
/// 详见 docs/需求规范-软件升级检查.md（如未来落地）与 CLAUDE.md §9.5 分发渠道例外。
/// 命名 HttpClient：<see cref="HttpClientName"/>；PmmHost.CreateApp 用 AddHttpClient(HttpClientName)
/// 集中配置 BaseAddress / 默认 Header / 超时（30s）。
///
/// 不引 Polly：参考 TmdbConnectivityTester 的极简风格，调用频率极低（启动 + 24h 周期），不值得装 Polly。
/// HTTP 状态码处理：
///   - 200 → 解析 JSON 返 Success=true + Latest
///   - 304 → 命中 ETag，NotModified=true（调用方继续用上次缓存）
///   - 401 → ErrorCategory="auth"
///   - 403 + X-RateLimit-Remaining=0 → ErrorCategory="rate_limit"
///   - 404 → ErrorCategory="not_found"（仓库不存在 / PAT 无权访问 / 仓库无 Release）
///   - 5xx / 网络 / 超时 → ErrorCategory="network"
///   - JSON 解析失败 → ErrorCategory="parse"
/// </remarks>
public sealed class GitHubUpdateChecker : IUpdateChecker
{
    /// <summary>命名 HttpClient 名（PmmHost 集中配置 BaseAddress / 默认 Header）</summary>
    public const string HttpClientName = "GitHubUpdateChecker";

    /// <summary>GitHub API 基址（不可改，强约束 v3 REST）</summary>
    private const string BaseUrl = "https://api.github.com";

    /// <summary>请求超时（30s 同 TMDB 探测）</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubUpdateChecker> _logger;

    public GitHubUpdateChecker(IHttpClientFactory httpFactory, ILogger<GitHubUpdateChecker> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<UpdateCheckOutcome> CheckAsync(
        string owner,
        string repo,
        string? pat,
        string currentVersion,
        string? etag,
        CancellationToken ct = default)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        Stopwatch sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            sw.Stop();
            return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                "not_found", "GitHub Owner 或 Repo 未配置", httpStatus: null);
        }

        string url = $"{BaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest";

        try
        {
            using HttpClient client = _httpFactory.CreateClient(HttpClientName);
            if (client.Timeout == TimeSpan.FromSeconds(100)) // SDK 默认值，外部未覆盖
            {
                client.Timeout = RequestTimeout;
            }

            using HttpRequestMessage req = new(HttpMethod.Get, url);
            // GitHub 强制 User-Agent，缺会 403：用产品名 + 当前版本号作为标识
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PersonalMediaManager",
                string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            if (!string.IsNullOrWhiteSpace(pat))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
            }

            if (!string.IsNullOrWhiteSpace(etag))
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using HttpResponseMessage resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            int status = (int)resp.StatusCode;
            sw.Stop();

            // 取响应 ETag（不论 200 / 304 / 401 都可能带）
            string? newEtag = resp.Headers.ETag?.Tag;

            // 304 Not Modified：调用方继续用上次缓存的 LatestVersion 与 etag
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                return new UpdateCheckOutcome(
                    Success: true,
                    NotModified: true,
                    HasNewVersion: false,
                    CurrentVersion: currentVersion,
                    Latest: null,
                    Etag: newEtag ?? etag,
                    HttpStatus: status,
                    ErrorCategory: null,
                    ErrorMessage: null,
                    CheckedAt: checkedAt,
                    ElapsedMilliseconds: sw.ElapsedMilliseconds);
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                    "auth", "GitHub PAT 无效或已过期", httpStatus: status);
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                // 区分 rate limit vs 普通禁止：rate limit 时 X-RateLimit-Remaining=0
                string? remaining = TryGetHeader(resp, "X-RateLimit-Remaining");
                if (remaining == "0")
                {
                    string? resetEpoch = TryGetHeader(resp, "X-RateLimit-Reset");
                    string suffix = !string.IsNullOrEmpty(resetEpoch) && long.TryParse(resetEpoch, out long epoch)
                        ? $"，将于 {DateTimeOffset.FromUnixTimeSeconds(epoch):yyyy-MM-dd HH:mm:ss} UTC 恢复"
                        : string.Empty;
                    return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                        "rate_limit", $"GitHub API 速率限制已达上限{suffix}", httpStatus: status);
                }
                return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                    "auth", "GitHub 拒绝请求（403），请检查 PAT 权限", httpStatus: status);
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                    "not_found", "仓库不存在或 PAT 无权访问，或仓库尚未发布任何 Release", httpStatus: status);
            }

            if (!resp.IsSuccessStatusCode)
            {
                string body = await SafeReadAsync(resp, ct);
                return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                    "network", $"GitHub 返回 HTTP {status}：{Truncate(body, 200)}", httpStatus: status);
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            GitHubLatestRelease? release;
            try
            {
                release = ParseRelease(json);
            }
            catch (JsonException ex)
            {
                return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                    "parse", $"GitHub 响应 JSON 解析失败：{ex.Message}", httpStatus: status);
            }

            if (release is null)
            {
                return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                    "parse", "GitHub 响应缺少 tag_name 字段", httpStatus: status);
            }

            bool hasNew = SemVerComparer.IsNewer(release.Version, currentVersion);

            return new UpdateCheckOutcome(
                Success: true,
                NotModified: false,
                HasNewVersion: hasNew,
                CurrentVersion: currentVersion,
                Latest: release,
                Etag: newEtag,
                HttpStatus: status,
                ErrorCategory: null,
                ErrorMessage: null,
                CheckedAt: checkedAt,
                ElapsedMilliseconds: sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                "network", $"GitHub 请求超时（{RequestTimeout.TotalSeconds:0}s）", httpStatus: null);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "GitHub 检查更新网络异常：Owner={Owner} Repo={Repo}", owner, repo);
            return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                "network", $"网络错误：{ex.Message}", httpStatus: null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "GitHub 检查更新未预期异常：Owner={Owner} Repo={Repo}", owner, repo);
            return Failure(checkedAt, sw.ElapsedMilliseconds, currentVersion,
                "network", $"未预期错误：{ex.Message}", httpStatus: null);
        }
    }

    /// <summary>从 GitHub 原始 JSON 提取关键字段（手写解析，避免与 SDK SnakeCaseLower 命名策略耦合）</summary>
    private static GitHubLatestRelease? ParseRelease(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? tagName = GetStr(root, "tag_name");
        if (string.IsNullOrEmpty(tagName)) return null;

        string version = tagName.TrimStart('v', 'V').Trim();
        string name = GetStr(root, "name") ?? tagName;
        string body = GetStr(root, "body") ?? string.Empty;
        string htmlUrl = GetStr(root, "html_url") ?? string.Empty;
        bool prerelease = root.TryGetProperty("prerelease", out JsonElement pre)
            && pre.ValueKind == JsonValueKind.True;
        bool draft = root.TryGetProperty("draft", out JsonElement dr)
            && dr.ValueKind == JsonValueKind.True;

        DateTimeOffset publishedAt = DateTimeOffset.MinValue;
        string? publishedStr = GetStr(root, "published_at");
        if (!string.IsNullOrEmpty(publishedStr)
            && DateTimeOffset.TryParse(publishedStr, out DateTimeOffset parsed))
        {
            publishedAt = parsed;
        }

        return new GitHubLatestRelease(tagName, version, name, body, htmlUrl, prerelease, draft, publishedAt);
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        return v.GetString();
    }

    private static string? TryGetHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return values.FirstOrDefault();
        }
        return null;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static UpdateCheckOutcome Failure(
        DateTimeOffset checkedAt,
        long elapsedMs,
        string currentVersion,
        string category,
        string message,
        int? httpStatus)
        => new(
            Success: false,
            NotModified: false,
            HasNewVersion: false,
            CurrentVersion: currentVersion,
            Latest: null,
            Etag: null,
            HttpStatus: httpStatus,
            ErrorCategory: category,
            ErrorMessage: message,
            CheckedAt: checkedAt,
            ElapsedMilliseconds: elapsedMs);
}
