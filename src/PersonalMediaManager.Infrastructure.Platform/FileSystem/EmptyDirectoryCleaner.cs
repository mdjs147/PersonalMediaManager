using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.Platform.FileSystem;

/// <summary>IEmptyDirectoryCleaner 实现 — 归档后向上回收空 / 仅含可忽略残留的源目录</summary>
/// <remarks>
/// 算法：
/// - 从 startDirectory 起逐层向父目录上溯，到 boundary（监控根）为止，绝不删除 boundary 本身。
/// - 每层判「可视为空」：目录内除 ignoreExtensions（如 .torrent）外无其它文件，且子目录递归同样可视为空。
///   命中即 Directory.Delete(recursive)，连可忽略残留与空子目录一并删除。
/// - 一旦遇到含真实内容的目录立即停止上溯（它非空，其祖先更不可能空）。
/// - 起点不在 boundary 之下 / 枚举失败 / 删除失败：一律保守跳过或停止，绝不误删 boundary 外或含内容的目录。
///
/// 路径比较按 Windows 语义（OrdinalIgnoreCase + 规范化去尾分隔符）；产品仅支持 Windows。
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
        if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(boundary))
            return deleted;

        string boundaryFull = Normalize(boundary);
        string? current = Normalize(startDirectory);

        // 起点必须严格位于 boundary 之下：防越界删到监控根外，亦防把监控根自身当空目录删
        if (!IsStrictlyUnder(current, boundaryFull))
        {
            _logger.LogDebug("源目录不在监控根之下，跳过空目录清理：{Start}（根 {Boundary}）", startDirectory, boundary);
            return deleted;
        }

        while (current is not null
            && !PathEquals(current, boundaryFull)
            && IsStrictlyUnder(current, boundaryFull))
        {
            ct.ThrowIfCancellationRequested();
            string? parent = Path.GetDirectoryName(current);

            if (!Directory.Exists(current))
            {
                // 目录已不存在（外部并发清理 / 上一轮已删）：继续上溯检查父目录
                current = parent;
                continue;
            }

            if (!IsEffectivelyEmpty(current, ignoreExtensions))
                break; // 含真实内容：它非空，祖先更不可能空，停止上溯

            try
            {
                Directory.Delete(current, recursive: true); // 连可忽略残留 + 空子目录一并删
                deleted.Add(current);
                _logger.LogInformation("回收源端空目录：{Path}", current);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "空目录删除失败（跳过，停止上溯）：{Path}", current);
                break;
            }

            current = parent;
        }

        return deleted;
    }

    /// <summary>目录是否「可视为空」：仅含可忽略扩展名文件，且所有子目录递归同样可视为空</summary>
    /// <remarks>枚举失败（权限 / 网络盘抖动）一律视为非空，绝不误删。</remarks>
    private static bool IsEffectivelyEmpty(string dir, IReadOnlySet<string> ignoreExtensions)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
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

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>child 是否严格位于 parent 之下（不含 parent 自身）；两者均须已规范化</summary>
    private static bool IsStrictlyUnder(string child, string parent)
        => child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
