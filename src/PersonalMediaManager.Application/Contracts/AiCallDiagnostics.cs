namespace PersonalMediaManager.Application.Contracts;

/// <summary>AI 调用失败路径经 Exception.Data 透传诊断原文/状态的键</summary>
/// <remarks>
/// 接口故障路径靠抛异常驱动编排层的分类升级（Transient/RateLimit/Logical），无法用返回值携带诊断原文；
/// 故约定：协议层（AiHttpFailureMapper）在抛异常前把「响应体 + HTTP 状态码」塞进 <see cref="System.Exception.Data"/>，
/// 解析门面（AiProtocolParser）补挂「请求原文」（及 JSON 反解失败时的响应原文 + token），
/// 编排层（AiCallOrchestrator）catch 后用本类键读出，连同成功路径数据统一落 Audit_AiCall。
/// 用 Exception.Data 而非给三个异常类各加属性：零类型改动、bare <c>throw;</c> 即保留原始堆栈、跨程序集可读。
/// 键值约定（装箱）：RequestText/ResponseText 为 string，HttpStatus/PromptTokens/CompletionTokens 为 int（可空时不写键）。
/// </remarks>
public static class AiCallDiagnostics
{
    public const string RequestTextKey = "pmm.ai.requestText";
    public const string ResponseTextKey = "pmm.ai.responseText";
    public const string HttpStatusKey = "pmm.ai.httpStatus";
    public const string PromptTokensKey = "pmm.ai.promptTokens";
    public const string CompletionTokensKey = "pmm.ai.completionTokens";
}
