using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Tmdb;

/// <summary>TMDB API 最小连通性探测</summary>
/// <remarks>
/// 端点：GET https://api.themoviedb.org/3/configuration（轻量，无需查询参数）。
/// 鉴权：v4 Bearer Token（Authorization: Bearer {ApiKey}）；TMDB v3 也接受这种方式。
/// 异常归集为 Success=false + ErrorMessage 不抛；完整 TmdbClient（缓存 / Polly / 候选排序）留 D.2。
/// </remarks>
internal sealed class TmdbConnectivityTester : ITmdbConnectivityTester
{
    private const string ConfigurationUrl = "https://api.themoviedb.org/3/configuration";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TmdbConnectivityTester> _logger;

    public TmdbConnectivityTester(IHttpClientFactory httpFactory, ILogger<TmdbConnectivityTester> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<TmdbConnectivityTestResult> TestAsync(string apiKey, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new TmdbConnectivityTestResult(false, null, 0, "ApiKey 不能为空");

        HttpClient client = _httpFactory.CreateClient("TmdbConnectivityTester");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, Math.Min(timeoutSeconds, 60)));

        HttpRequestMessage req = new(HttpMethod.Get, ConfigurationUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            HttpResponseMessage resp = await client.SendAsync(req, ct);
            sw.Stop();
            return new TmdbConnectivityTestResult(
                resp.IsSuccessStatusCode,
                (int)resp.StatusCode,
                sw.Elapsed.TotalMilliseconds,
                resp.IsSuccessStatusCode ? null : resp.ReasonPhrase);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new TmdbConnectivityTestResult(false, null, sw.Elapsed.TotalMilliseconds, $"超时（{timeoutSeconds}s）");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "TMDB 连通性探测异常");
            return new TmdbConnectivityTestResult(false, null, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }
}
