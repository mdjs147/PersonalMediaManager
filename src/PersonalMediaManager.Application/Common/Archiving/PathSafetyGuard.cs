namespace PersonalMediaManager.Application.Common.Archiving;

/// <summary>归档前路径穿越校验（D4.5）— 目标路径必须严格在分类根目录下</summary>
/// <remarks>
/// 防御场景：恶意 TMDB 标题（含 ../../ 或绝对路径片段）+ 文件名清洗漏网 → 归档到分类根目录之外（覆盖系统文件）。
/// 检查逻辑：
///   1. 把分类根目录与目标路径都规范化（Path.GetFullPath）：消除 . / .. / 多斜杠
///   2. 规范化后的目标路径必须以「规范化根目录 + 目录分隔符」开头（用平台正确分隔符 + 大小写敏感性）
///   3. Windows 下文件路径大小写不敏感，但归一化后比较走 OrdinalIgnoreCase；Linux 走 Ordinal
///
/// 边界：分类根本身（无子路径）也算违规（归档必须落在子目录里，不能覆盖根目录占位文件）。
/// </remarks>
public static class PathSafetyGuard
{
    /// <summary>校验 targetPath 是否在 categoryRoot 下；不在 → 抛 PathTraversalException</summary>
    public static void EnsureWithinRoot(string categoryRoot, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(categoryRoot))
            throw new ArgumentException("分类根目录不能为空", nameof(categoryRoot));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("目标路径不能为空", nameof(targetPath));

        string normRoot = NormalizeWithTrailingSeparator(categoryRoot);
        string normTarget = Path.GetFullPath(targetPath);

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normTarget.StartsWith(normRoot, cmp))
            throw new PathTraversalException(categoryRoot, targetPath);
        if (normTarget.Length == normRoot.Length)
            throw new PathTraversalException(categoryRoot, targetPath); // 命中根目录本身
    }

    /// <summary>判定（不抛异常）</summary>
    public static bool IsWithinRoot(string categoryRoot, string targetPath)
    {
        try
        {
            EnsureWithinRoot(categoryRoot, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is PathTraversalException || ex is ArgumentException)
        {
            return false;
        }
    }

    /// <summary>归一化并保证结尾有目录分隔符（用于 StartsWith 比较）</summary>
    private static string NormalizeWithTrailingSeparator(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length == 0 || full[^1] != Path.DirectorySeparatorChar)
            full += Path.DirectorySeparatorChar;
        return full;
    }
}

/// <summary>路径穿越拦截</summary>
public sealed class PathTraversalException : Exception
{
    public PathTraversalException(string categoryRoot, string targetPath)
        : base($"目标路径越出分类根：root={categoryRoot} target={targetPath}")
    {
        CategoryRoot = categoryRoot;
        TargetPath = targetPath;
    }

    public string CategoryRoot { get; }
    public string TargetPath { get; }
}
