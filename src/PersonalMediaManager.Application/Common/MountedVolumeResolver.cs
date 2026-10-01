namespace PersonalMediaManager.Application.Common;

/// <summary>按路径段边界解析所在挂载卷</summary>
public static class MountedVolumeResolver
{
    /// <summary>Windows 保留盘符/UNC；Unix 选择最长匹配挂载点</summary>
    public static string? TryResolve(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Path.GetPathRoot(Path.GetFullPath(path));
            return TryResolveUnix(path, () => DriveInfo.GetDrives().Select(drive => drive.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // 注入纯挂载点数据便于测试；不可用的卷仍保留身份，由调用方探测失败后跳过，不能退回系统盘。
    internal static string? TryResolveUnix(string path, Func<IEnumerable<string>> getMounts)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string? best = null;
            foreach (string mount in getMounts())
            {
                if (!Path.IsPathFullyQualified(mount)) continue;
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount));
                if (PlatformPaths.Comparer.Equals(full, root) || PlatformPaths.IsWithinDirectory(full, root))
                {
                    if (best is null || root.Length > best.Length) best = root;
                }
            }
            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
