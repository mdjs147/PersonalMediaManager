namespace PersonalMediaManager.Application.Contracts;

/// <summary>当前启用扩展名集合提供方（内存缓存，Singleton）</summary>
/// <remarks>
/// 供 FileIntakeService / ScanService 等高频路径使用，不做任何 I/O，直接返回缓存集合。
/// CRUD 变更（Create / Update / Delete）完成后，实现方自动刷新缓存。
/// 启动时由 MediaExtensionService 在首次 CRUD 调用前预热；启动阶段 GetEnabledExtensions()
/// 在缓存未就绪时降级返回编译期内置白名单，保证文件监控正常工作。
/// </remarks>
public interface IMediaExtensionProvider
{
    /// <summary>获取当前启用的扩展名集合（含前导点，OrdinalIgnoreCase）</summary>
    IReadOnlySet<string> GetEnabledExtensions();

    /// <summary>判断文件名/路径的最后一段扩展名是否为受支持视频格式</summary>
    bool IsVideo(string fileNameOrPath);

    /// <summary>异步刷新内存缓存（启动预热 / CRUD 变更后调用）</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
