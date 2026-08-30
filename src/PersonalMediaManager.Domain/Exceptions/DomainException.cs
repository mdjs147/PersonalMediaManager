namespace PersonalMediaManager.Domain.Exceptions;

/// <summary>领域层异常基类</summary>
/// <remarks>
/// 领域内不变式被破坏 / 非法状态转移时抛出；Application 层负责捕获并转 BusinessException（API code 1000）。
/// 领域不引 Application，故此处不耦合 ApiCode；纯字符串 message 即可。
/// </remarks>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
