namespace PersonalMediaManager.Application.Common;

/// <summary>操作系统保守路径比较规则</summary>
public static class PlatformPaths
{
    // macOS 也可能使用大小写敏感卷，不能把不同目录合并为同一缓存键。
    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>文件名主体相同或以字幕分隔符延伸</summary>
    public static bool MatchesFileStem(string fileStem, string sourceStem)
        => fileStem.StartsWith(sourceStem, Comparison)
            && (fileStem.Length == sourceStem.Length || fileStem[sourceStem.Length] is '.' or '-' or '_');

    /// <summary>检查规范化路径是否严格位于指定目录内</summary>
    public static bool IsWithinDirectory(string path, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return !Comparer.Equals(candidate, root) && candidate.StartsWith(prefix, Comparison);
    }
}
