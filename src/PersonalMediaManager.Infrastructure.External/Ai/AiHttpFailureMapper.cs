using System.Net;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>AI 协议策略共用的 HTTP 失败映射</summary>
/// <remarks>
/// 把各协议策略重复的「HTTP 状态 → 三类异常」与「catch 块 → 三类异常」收敛到一处，所有 <see cref="IAiProtocol"/> 实现复用，
/// 保证失败语义在全协议口径一致：
///   - HTTP 429 → <see cref="AiProviderRateLimitException"/>（已达上限，上层立即升级到更高级 AI，不在本级空耗重试）
///   - HTTP &gt;= 500 → <see cref="AiProviderTransientException"/>（服务端瞬时故障，允许内部短重试）
///   - HTTP &gt;= 400 → <see cref="AiProviderLogicalException"/>（携带状态码，4xx 逻辑错误）
///   - <see cref="HttpRequestException"/>（DNS / TCP 连接失败）→ Transient
///   - <see cref="TaskCanceledException"/>：在 <see cref="AiPromptHelpers.TransientFirstByteThreshold"/>（5s）内取消 → 首字节超时归 Transient，超过 → 请求超时归 Logical
/// 用法：协议策略 try 内调 <see cref="ThrowForStatus"/>，catch 内一行调 <see cref="MapSendException"/> 重抛即可。
/// </remarks>
internal static class AiHttpFailureMapper
{
    /// <summary>按 HTTP 状态码抛对应失败异常；状态码 &lt; 400（成功 / 重定向）时直接返回不抛</summary>
    /// <remarks>
    /// <paramref name="protocol"/> 仅用于拼接异常文案（如 "OpenAiCompatible"），便于日志区分来源；
    /// <paramref name="body"/> 为响应体（调用方读失败可传空串），内部自动截断 200 字符防日志爆量。
    /// </remarks>
    public static void ThrowForStatus(string protocol, HttpStatusCode statusCode, string body)
    {
        int status = (int)statusCode;
        if (status < 400)
            return;

        string snippet = AiPromptHelpers.Truncate(body, 200);
        Exception ex = statusCode == HttpStatusCode.TooManyRequests
            ? new AiProviderRateLimitException($"{protocol} HTTP 429 限流/配额（body={snippet}）")
            : status >= 500
                ? new AiProviderTransientException($"{protocol} HTTP {status}（body={snippet}）")
                : new AiProviderLogicalException($"{protocol} HTTP {status}（body={snippet}）", status);
        // 监控诊断：完整响应体 + 状态码塞入 Exception.Data，由编排层落 Audit_AiCall.ResponseText / HttpStatus
        ex.Data[AiCallDiagnostics.ResponseTextKey] = body;
        ex.Data[AiCallDiagnostics.HttpStatusKey] = status;
        throw ex;
    }

    /// <summary>把 SendAsync 抛出的网络/超时异常映射为契约异常（始终抛出，不返回）</summary>
    /// <remarks>
    /// <paramref name="sentAt"/> 为请求发出时刻（UTC），用于和当前时刻求耗时，判定 <see cref="TaskCanceledException"/> 落在首字节超时窗口内（Transient）还是窗口外（Logical）。
    /// <b>注意</b>：调用方需先用 <c>when (!ct.IsCancellationRequested)</c> 过滤主动取消——主动取消不应进入本方法（属正常停止，非失败）。
    /// 非 <see cref="HttpRequestException"/> / <see cref="TaskCanceledException"/> 的异常按瞬时兜底（保守允许短重试）。
    /// </remarks>
    /// <returns>形式上返回 <see cref="Exception"/> 以支持 <c>throw AiHttpFailureMapper.MapSendException(...)</c> 写法；实际本方法体内即抛出，永不正常返回。</returns>
    public static Exception MapSendException(string protocol, Exception ex, DateTimeOffset sentAt)
    {
        switch (ex)
        {
            case HttpRequestException:
                throw new AiProviderTransientException($"{protocol} 网络异常：{ex.Message}", ex);
            case TaskCanceledException:
                TimeSpan elapsed = DateTimeOffset.UtcNow - sentAt;
                if (elapsed < AiPromptHelpers.TransientFirstByteThreshold)
                    throw new AiProviderTransientException($"{protocol} 首字节超时（{elapsed.TotalMilliseconds:F0}ms）", ex);
                throw new AiProviderLogicalException($"{protocol} 请求超时（{elapsed.TotalMilliseconds:F0}ms）", inner: ex);
            default:
                throw new AiProviderTransientException($"{protocol} 请求异常：{ex.Message}", ex);
        }
    }
}
