using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>AI 解析门面契约（协议无关）</summary>
/// <remarks>
/// 在 <see cref="IAiProtocol"/>（纯 wire 原语）之上的应用层薄封装：把「媒体文件名解析」专用的 system / user 提示词构造，
/// 与「原始文本 → {title,year,type,...}」JSON 反解收敛到一处，供 <c>AiCallOrchestrator</c>（Application 层）调用。
/// 之所以单立此契约：Application 不得直接依赖 Infrastructure.External 的提示词 / 反解工具（违反引用方向），
/// 故由 External 的 <c>AiProtocolParser</c> 实现本接口并持有全部 <see cref="IAiProtocol"/>，按协议路由。
/// 失败语义沿用协议原语（抛 AiProviderTransient / RateLimit / Logical 异常），由编排层据此分类升级。
/// </remarks>
public interface IAiParser
{
    /// <summary>是否存在该协议的实现</summary>
    /// <remarks>编排层用于「配置漂移」判定：DB 里的协议在 DI 没有对应实现时跳过该级且不消耗级数额度。</remarks>
    bool Supports(AiProviderType protocol);

    /// <summary>用指定协议调一次 AI 解析：拼解析提示词 → 协议原语补全 → 反解为结构化结果（含原文 + token）</summary>
    /// <remarks>
    /// 返回 <see cref="AiParseOutcome"/>：结构化结果外附带请求/响应原文与 token 用量，供编排层落 Audit_AiCall 做监控诊断。
    /// 接口故障仍抛三类异常；其诊断原文（响应体/状态码/请求原文）经 <see cref="AiCallDiagnostics"/>（Exception.Data）携带。
    /// </remarks>
    /// <exception cref="AiProviderTransientException">瞬时错误（DNS/TCP/首字节超时 &lt; 5s / 5xx）</exception>
    /// <exception cref="AiProviderRateLimitException">限流 / 配额（HTTP 429）</exception>
    /// <exception cref="AiProviderLogicalException">逻辑错误（4xx / JSON 反解失败 / schema 不符 / 无该协议实现）</exception>
    Task<AiParseOutcome> ParseAsync(
        AiProviderType protocol,
        AiProviderEndpoint endpoint,
        AiParseRequest request,
        CancellationToken ct = default);
}
