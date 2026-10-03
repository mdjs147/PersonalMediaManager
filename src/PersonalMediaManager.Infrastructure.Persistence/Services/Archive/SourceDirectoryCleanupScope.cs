using System.Text;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

/// <summary>清理子树的 SQL 宽筛与平台路径精确范围</summary>
/// <remarks>比较方式显式传入以便测试 Windows 语义；SQL LIKE 只作超集筛选，不能代替平台路径判断。</remarks>
internal sealed class SourceDirectoryCleanupScope
{
    internal SourceDirectoryCleanupScope(string directory, StringComparison comparison)
    {
        if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(comparison), "清理路径只支持平台序数比较");
        Comparison = comparison;
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Prefix = Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;
        LikePattern = BuildLikePattern(Prefix, comparison);
    }

    internal static SourceDirectoryCleanupScope ForPlatform(string directory) => new(directory, PlatformPaths.Comparison);
    internal StringComparison Comparison { get; }
    internal string Prefix { get; }
    internal string LikePattern { get; }

    internal bool Contains(string sourcePath) => Path.GetFullPath(sourcePath).StartsWith(Prefix, Comparison);

    private static string BuildLikePattern(string prefix, StringComparison comparison)
    {
        StringBuilder pattern = new();
        foreach (Rune rune in prefix.EnumerateRunes())
        {
            // SQLite LIKE 只折叠 ASCII：非 ASCII 字符按 Unicode 标量放宽为单字符，
            // 保留完整目录层级与其余字面量，避免退化为无路径约束的全媒体读取。
            if (comparison == StringComparison.OrdinalIgnoreCase && !rune.IsAscii)
            {
                pattern.Append('_');
                continue;
            }
            string value = rune.ToString();
            if (value is "!" or "%" or "_") pattern.Append('!');
            pattern.Append(value);
        }
        return pattern.Append('%').ToString();
    }
}
