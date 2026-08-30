namespace PersonalMediaManager.Application.Common;

/// <summary>API 响应 code（极简 3 码原则，CLAUDE.md §六）</summary>
/// <remarks>
/// 0 = Success：业务成功
/// 1000 = BusinessError：通用业务失败（参数 / 不存在 / 冲突 / 校验 / 超时 / 幂等重复全部归此码）
/// 9000 = ServerError：服务器或基础设施错误（DB / 缓存 / MQ / 外部服务 / 未预期）
/// 最小 code 原则：同类失败用同一 code + 不同 message；只有前端行为不同才新增 code。
/// </remarks>
public static class ApiCode
{
    public const int Success = 0;
    public const int BusinessError = 1000;
    public const int ServerError = 9000;
}
