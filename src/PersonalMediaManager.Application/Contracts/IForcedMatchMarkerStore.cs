using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>强制匹配标识读取契约（D7）—— 从被处理文件所在文件夹链路读取 pmm.txt / .url 标识</summary>
/// <remarks>
/// 实现在 Infrastructure.Platform（文件 IO 归 Platform 层）：自文件所在目录起逐层上溯至监控根，
/// 命中最近一个 pmm.txt（或含 themoviedb.org 的 .url 快捷方式）即解析返回；无则返回 null。
/// 读取/解析失败一律吞为 null + 记日志，绝不让标识问题打断主处理管线。
/// </remarks>
public interface IForcedMatchMarkerStore
{
    /// <summary>读取并解析最近的强制匹配标识；无标识或无效返回 null</summary>
    Task<ForcedMatchMarker?> TryReadAsync(FileParseContext ctx, CancellationToken ct = default);
}
