using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>AI 提供商连通性测试契约（C.5 /test 端点使用）</summary>
/// <remarks>
/// 实现在 Infrastructure.External 走 HttpClientFactory；
/// 本接口仅做最小连通性探测，不发完整解析提示词。完整的 AI 解析在 D.3 IAiParser 落地。
/// </remarks>
public interface IAiProviderTester
{
    Task<AiProviderTestResult> TestAsync(
        AiProviderType type,
        string baseUrl,
        string? apiKey,
        string model,
        int timeoutSeconds,
        bool useProxy,
        CancellationToken ct = default);
}

/// <summary>探测结果</summary>
public sealed record AiProviderTestResult(
    bool Success,
    int? HttpStatus,
    double ElapsedMilliseconds,
    string? ErrorMessage,
    string? ResponseSnippet);
