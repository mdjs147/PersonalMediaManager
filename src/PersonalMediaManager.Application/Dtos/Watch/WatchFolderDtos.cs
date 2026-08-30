using System.ComponentModel.DataAnnotations;
using PersonalMediaManager.Application.Common.Validation;

namespace PersonalMediaManager.Application.Dtos.Watch;

/// <summary>监控目录响应 DTO</summary>
/// <remarks>
/// MediaCount：本目录下（SourcePath = Path 或以 Path+分隔符 开头）的 Media_Item 总数，
/// 用于 Dashboard 卡片实时展示「监控媒体数」统计；CRUD 路径默认 0（创建/更新瞬间不聚合）。
/// RowVersion：给前端用于乐观并发——Update 时回传，服务端比对不一致则 1000「请刷新后重试」。
/// </remarks>
public sealed record WatchFolderResponse(
    long Id,
    string Path,
    string? Alias,
    bool IsTransit,
    bool IsNetworkShare,
    bool Enabled,
    bool AutoScan,
    int Priority,
    int MediaCount,
    DateTimeOffset? LastScanAt,
    DateTimeOffset? LastReachableAt,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateWatchFolderRequest(
    [RequiredNotBlank(ErrorMessage = "路径不能为空")]
    [MaxLength(500, ErrorMessage = "路径长度超过 500 字符")]
    string Path,
    [MaxLength(64, ErrorMessage = "Alias 长度不能超过 64 字符")]
    string? Alias,
    bool IsTransit,
    bool IsNetworkShare,
    bool Enabled = true,
    bool AutoScan = true,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority = 100);

public sealed record UpdateWatchFolderRequest(
    long Id,
    [RequiredNotBlank(ErrorMessage = "路径不能为空")]
    [MaxLength(500, ErrorMessage = "路径长度超过 500 字符")]
    string Path,
    [MaxLength(64, ErrorMessage = "Alias 长度不能超过 64 字符")]
    string? Alias,
    bool IsTransit,
    bool IsNetworkShare,
    bool Enabled,
    bool AutoScan,
    [Range(0, 9999, ErrorMessage = "Priority 必须在 [0, 9999] 范围内")]
    int Priority,
    long RowVersion);

public sealed record DeleteWatchFolderRequest(long Id);

/// <summary>监控目录连通性测试结果（/{id}/test 返回）</summary>
public sealed record WatchFolderTestResponse(
    bool Reachable,
    bool DirectoryExists,
    string? SampleEntry,
    string? ErrorMessage);
