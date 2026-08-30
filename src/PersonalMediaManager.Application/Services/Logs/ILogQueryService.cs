using PersonalMediaManager.Application.Dtos.Logs;

namespace PersonalMediaManager.Application.Services.Logs;

/// <summary>日志文件分页查询（D7.9）— API §2.7 /logs</summary>
/// <remarks>
/// 走 AppPaths.LogDir 下 pmm-*.log 文件分页读取，不入库（需求文档 §3.11）。
/// total 是粗略估值（实现侧扫到匹配的行数），仅供前端展示进度。
/// </remarks>
public interface ILogQueryService
{
    Task<LogPage> ListAsync(LogQuery query, CancellationToken ct = default);
}
