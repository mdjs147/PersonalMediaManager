namespace PersonalMediaManager.Application.Services.Audit;

/// <summary>Audit_Operation 写入器（B6.4）</summary>
/// <remarks>
/// 由各 Application Service 在关键写操作后调用，或由 Host ActionFilter 自动写。
/// Detail 推荐 JSON 字符串（变更前后或附加信息）；Ip / UserAgent 由 Host 端从 HttpContext 取。
/// </remarks>
public interface IAuditOperationWriter
{
    Task WriteAsync(
        long? userId,
        string? username,
        string action,
        string? target = null,
        string? detail = null,
        string? ip = null,
        string? userAgent = null,
        CancellationToken ct = default);
}
