using System.Collections.Concurrent;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>IFolderSeriesCache 进程内实现</summary>
/// <remarks>
/// 键 = 目录完整路径；Windows 忽略大小写，其余平台保守区分，兼容大小写敏感卷。
/// 无 TTL：媒体库目录数量有限、单条仅数十字节，长期堆积可忽略；进程重启自然清空。
/// 主动失效：用户在审核页纠正错误匹配后由 ReviewService 调 Remove，防同目录后续文件复用旧错误 tmdbId。
/// </remarks>
internal sealed class FolderSeriesCache : IFolderSeriesCache
{
    private readonly ConcurrentDictionary<string, FolderSeriesEntry> _map =
        new(PlatformPaths.Comparer);

    public FolderSeriesEntry? TryGet(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return null;
        return _map.TryGetValue(folderPath, out FolderSeriesEntry? entry) ? entry : null;
    }

    public void Set(string folderPath, FolderSeriesEntry entry)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        _map[folderPath] = entry;
    }

    public void Remove(string folderPath)
    {
        // 用户纠错后失效：键不存在时静默幂等（与 Set / TryGet 共用平台路径比较器）
        if (string.IsNullOrEmpty(folderPath)) return;
        _map.TryRemove(folderPath, out _);
    }
}
