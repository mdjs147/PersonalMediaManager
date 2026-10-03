using PersonalMediaManager.Application.Common;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>IEmptyDirectoryCleaner 实现 — 归档后向上回收空 / 仅含可忽略残留的源目录</summary>
/// <remarks>
/// 算法：
/// - 从 startDirectory 起逐层向父目录上溯，到 boundary（监控根）为止，绝不删除 boundary 本身。
/// - 每层判「可视为空」：目录内除 ignoreExtensions（如 .torrent）外无其它文件，且子目录递归同样可视为空。
///   命中后逐项清理可忽略残留与空子目录；目录使用非递归删除，避免吞掉新到达文件。
/// - 一旦遇到含真实内容的目录立即停止上溯（它非空，其祖先更不可能空）。
/// - 起点不在 boundary 之下 / 枚举失败 / 删除失败：一律保守跳过或停止，绝不误删 boundary 外或含内容的目录。
///
/// 路径比较使用平台语义；任何符号链接子树均保守跳过。
/// 单进程串行处理（TaskProcessorWorker 全局 SemaphoreSlim(1,1)）下不存在并发清理同一子树的竞争。
/// </remarks>
internal sealed class EmptyDirectoryCleaner : IEmptyDirectoryCleaner
{
    private readonly ILogger<EmptyDirectoryCleaner> _logger;

    public EmptyDirectoryCleaner(ILogger<EmptyDirectoryCleaner> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> CleanUpward(
        string startDirectory,
        string boundary,
        IReadOnlySet<string> ignoreExtensions,
        CancellationToken ct = default)
    {
        List<string> deleted = new();
        foreach (string candidate in EnumerateCandidates(startDirectory, boundary, ignoreExtensions, ct))
        {
            if (!TryDelete(candidate, boundary, ignoreExtensions, ct)) break;
            deleted.Add(candidate);
        }
        return deleted;
    }

    public async Task<IReadOnlyList<string>> CleanUpwardAsync(
        string startDirectory,
        string boundary,
        IReadOnlySet<string> ignoreExtensions,
        Func<string, CancellationToken, Task<bool>> canDeleteDirectory,
        CancellationToken ct = default)
    {
        List<string> deleted = new();
        foreach (string candidate in EnumerateCandidates(startDirectory, boundary, ignoreExtensions, ct))
        {
            // 判断覆盖整个子树，不能只检查触发归档的最后一集或最后一部电影。
            if (!await canDeleteDirectory(candidate, ct)) break;
            ct.ThrowIfCancellationRequested();
            if (!TryDelete(candidate, boundary, ignoreExtensions, ct)) break;
            deleted.Add(candidate);
        }
        return deleted;
    }

    private IEnumerable<string> EnumerateCandidates(
        string startDirectory, string boundary, IReadOnlySet<string> ignoreExtensions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(boundary)) yield break;
        string boundaryFull = Normalize(boundary);
        string? current = Normalize(startDirectory);
        if (!IsStrictlyUnder(current, boundaryFull) || HasLinkedAncestor(current, boundaryFull))
        {
            _logger.LogDebug("源目录不在监控根之下或包含链接，跳过空目录清理：{Start}（根 {Boundary}）", startDirectory, boundary);
            yield break;
        }
        while (current is not null && IsStrictlyUnder(current, boundaryFull))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(current))
            {
                if (!IsEffectivelyEmpty(current, ignoreExtensions)) yield break;
                yield return current;
            }
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>业务复核后再次检查边界和内容，逐项删除残留并用非递归删除保留新到达文件</summary>
    private bool TryDelete(string directory, string boundary, IReadOnlySet<string> ignoreExtensions, CancellationToken ct)
    {
        try
        {
            if (!IsStrictlyUnder(directory, Normalize(boundary))
                || HasLinkedAncestor(directory, Normalize(boundary))
                || !IsEffectivelyEmpty(directory, ignoreExtensions)) return false;
            DeleteEmptyTree(directory, Normalize(boundary), ignoreExtensions, ct);
            _logger.LogInformation("回收源端空目录：{Path}", directory);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "空目录删除失败（跳过，停止上溯）：{Path}", directory);
            return false;
        }
    }

    private static void DeleteEmptyTree(
        string directory, string boundary, IReadOnlySet<string> ignoreExtensions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (HasLinkedAncestor(directory, boundary) || !IsEffectivelyEmpty(directory, ignoreExtensions))
            throw new IOException("源目录内容或链接发生变化，停止清理");
        foreach (string child in Directory.EnumerateDirectories(directory))
            DeleteEmptyTree(child, boundary, ignoreExtensions, ct);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0
                || !ignoreExtensions.Contains(Path.GetExtension(file)))
                throw new IOException("源目录出现不可忽略文件，停止清理");
            File.Delete(file);
        }
        // 不做递归删除：枚举后新出现的文件 / 子目录由操作系统阻止删除。
        Directory.Delete(directory, recursive: false);
    }

    /// <summary>目录是否「可视为空」：仅含可忽略扩展名文件，且所有子目录递归同样可视为空</summary>
    /// <remarks>枚举失败（权限 / 网络盘抖动）一律视为非空，绝不误删。</remarks>
    private static bool IsEffectivelyEmpty(string dir, IReadOnlySet<string> ignoreExtensions)
    {
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                string ext = Path.GetExtension(file);
                if (ext.Length == 0 || !ignoreExtensions.Contains(ext))
                    return false; // 无扩展名 / 不可忽略 = 真实内容文件
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (!IsEffectivelyEmpty(sub, ignoreExtensions))
                    return false; // 子目录非空
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 词法前缀不足以保护链接跳转；从起点到根的任一级链接都不能参与递归删除。
    private static bool HasLinkedAncestor(string start, string boundary)
    {
        try
        {
            for (string? path = start; path is not null; path = Path.GetDirectoryName(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
                if (PathEquals(path, boundary)) return false;
            }
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        return true;
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string a, string b)
        => string.Equals(a, b, PlatformPaths.Comparison);

    /// <summary>child 是否严格位于 parent 之下（不含 parent 自身）；两者均须已规范化</summary>
    private static bool IsStrictlyUnder(string child, string parent)
        => child.StartsWith(parent + Path.DirectorySeparatorChar, PlatformPaths.Comparison);
}
