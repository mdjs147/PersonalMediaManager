using System.ComponentModel.DataAnnotations;

namespace PersonalMediaManager.Application.Dtos.Logs;

/// <summary>日志查询参数（GET /logs）</summary>
/// <param name="Page">页码（≥ 1）</param>
/// <param name="PageSize">每页（上限 200）</param>
/// <param name="Level">级别过滤：Verbose/Debug/Information/Warning/Error/Fatal</param>
/// <param name="From">起始时间</param>
/// <param name="To">结束时间</param>
/// <param name="Keyword">消息文本模糊匹配</param>
/// <param name="Source">日志来源 class/category 子串匹配</param>
public sealed record LogQuery(
    [Range(1, int.MaxValue, ErrorMessage = "分页参数非法")]
    int Page = 1,
    [Range(1, 200, ErrorMessage = "分页参数非法")]
    int PageSize = 50,
    string? Level = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Keyword = null,
    string? Source = null);

public sealed record LogPage(
    IReadOnlyList<LogEntryResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record LogEntryResponse(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message);
