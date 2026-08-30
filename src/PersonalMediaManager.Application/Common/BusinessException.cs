namespace PersonalMediaManager.Application.Common;

/// <summary>业务异常（API code = 1000）</summary>
/// <remarks>
/// 应用服务遇到业务规则失败（参数/不存在/冲突/校验/超时/幂等重复）时抛出，
/// 由 Host/Middleware/ExceptionHandlerMiddleware 统一捕获 → 返 { code:1000, message, requestId }。
/// 不同于领域异常 DomainException —— 后者由应用服务捕获后转此异常再上抛。
/// </remarks>
public sealed class BusinessException : Exception
{
    public int Code => ApiCode.BusinessError;

    public BusinessException(string message) : base(message)
    {
    }

    public BusinessException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
