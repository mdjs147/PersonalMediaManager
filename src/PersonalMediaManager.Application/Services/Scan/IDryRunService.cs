using PersonalMediaManager.Application.Dtos.Scan;

namespace PersonalMediaManager.Application.Services.Scan;

/// <summary>整理演练服务契约 — 预览文件会被解析/归档成什么样，不产生任何副作用</summary>
/// <remarks>
/// 只读：规则解析 + TMDB 匹配（命中缓存不耗额度）+ Plex 命名预览；
/// 决策到「触发 AI」即停（不真正调用 AI，避免成本）；不移动文件、不创建 MediaItem。
/// 用 WatchFolderId=0 上下文，与「手动整理」走向一致。
/// </remarks>
public interface IDryRunService
{
    /// <summary>对单个文件路径做整理演练预览</summary>
    Task<DryRunPreview> PreviewAsync(string path, CancellationToken ct = default);
}
