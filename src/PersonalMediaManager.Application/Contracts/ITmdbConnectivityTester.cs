namespace PersonalMediaManager.Application.Contracts;

/// <summary>TMDB API 连通性测试契约（C.6 /test 端点使用）</summary>
/// <remarks>
/// 实现走 IHttpClientFactory，请求 https://api.themoviedb.org/3/configuration 携带 ApiKey；
/// 仅做活性 + 鉴权验证，不写库。完整 TmdbClient 在 D.2 落地。
/// </remarks>
public interface ITmdbConnectivityTester
{
    Task<TmdbConnectivityTestResult> TestAsync(string apiKey, int timeoutSeconds = 30, CancellationToken ct = default);
}

public sealed record TmdbConnectivityTestResult(
    bool Success,
    int? HttpStatus,
    double ElapsedMilliseconds,
    string? ErrorMessage);
