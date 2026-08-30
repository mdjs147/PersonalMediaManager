namespace PersonalMediaManager.Application.Services.Parse;

/// <summary>解析上下文 —— 把「监控根 → 文件」的完整路径段携带到规则引擎与 AI 兜底</summary>
/// <remarks>
/// 替代旧契约 `ParseAsync(fileName, parentFolderName)`：
///   单层父目录无法覆盖「中间层目录承载剧名/季信息」与「PT 站单层超长目录承载全部元信息」两类常见命名套路。
///
/// 字段约束：
///   FullPath 与 FileName 必填；
///   WatchRoot 与 RelativeSegments 在「手动扫描」/「单元测试」等无监控目录上下文场景下允许为空 —
///     此时规则引擎只能基于 FileName 单维度匹配（行为退回到旧契约的兜底模式）。
///
/// RelativeSegments 语义：
///   监控根（不含本身）→ 文件本身（不含文件名）之间的**目录段列表**，**外层 → 内层**。
///   不含文件名（文件名在 FileName 字段单独提供）。
///   示例：
///     FullPath = F:\迅雷下载\国务卿女士 6季\第1季\01.mp4
///     WatchRoot = F:\迅雷下载
///     FileName = 01.mp4
///     RelativeSegments = ["国务卿女士 6季", "第1季"]
///
///   特殊情况：文件直接在监控根下（如 F:\迅雷下载\01.mp4）→ RelativeSegments = [] 空集合，
///   规则引擎应按「无中间层目录」处理，等价于旧 parentFolderName=null。
/// </remarks>
public sealed record FileParseContext(
    string FullPath,
    string FileName,
    string? WatchRoot,
    IReadOnlyList<string> RelativeSegments)
{
    /// <summary>直接父目录名（RelativeSegments 末尾）；无中间层时为 null</summary>
    /// <remarks>
    /// 旧规则引擎调用方习惯单层父目录；本字段保留这一便利访问入口，避免新规则到处写 `RelativeSegments[^1]`。
    /// </remarks>
    public string? DirectParentFolderName =>
        RelativeSegments.Count == 0 ? null : RelativeSegments[^1];

    /// <summary>仅文件名模式：无监控根上下文（测试 / 手动扫描时）</summary>
    public static FileParseContext FileNameOnly(string fileName) =>
        new(FullPath: fileName, FileName: fileName, WatchRoot: null, RelativeSegments: Array.Empty<string>());

    /// <summary>从完整路径 + 监控根计算上下文；监控根为空时退化为 FileNameOnly + 直接父目录单段</summary>
    /// <remarks>
    /// 路径分隔符处理：Windows `\` / Linux `/` 都由 Path.GetRelativePath 与 Path.DirectorySeparatorChar 接管，
    /// 不手写 split 字符；可以正确处理 UNC 路径与混合分隔符。
    /// 监控根的「外侧」目录段不暴露给规则引擎（仅 RelativeSegments 内为候选解析目标）。
    /// </remarks>
    public static FileParseContext FromFullPath(string fullPath, string? watchRoot)
    {
        string fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(fileName))
        {
            // 极端情况：fullPath 以分隔符结尾、识别不到文件名 → 退化为按原始字符串当文件名
            return FileNameOnly(fullPath);
        }

        if (string.IsNullOrWhiteSpace(watchRoot))
        {
            // 无监控根 → 仅取直接父目录单段作为 RelativeSegments（向后兼容旧 parentFolderName 行为）
            string? dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir))
            {
                return new(fullPath, fileName, WatchRoot: null, Array.Empty<string>());
            }
            string parent = Path.GetFileName(dir);
            string[] singleSegment = string.IsNullOrEmpty(parent) ? Array.Empty<string>() : new[] { parent };
            return new(fullPath, fileName, WatchRoot: null, singleSegment);
        }

        string normalizedRoot = NormalizeDirPath(watchRoot);
        string fileDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

        // 文件不在监控根下（含跨盘符）→ 仍按「无监控根」退化处理，避免抛异常
        string relativeDir;
        try
        {
            relativeDir = Path.GetRelativePath(normalizedRoot, fileDir);
        }
        catch (ArgumentException)
        {
            return FromFullPath(fullPath, null);
        }

        // 退化：fileDir == normalizedRoot → GetRelativePath 返回 "."
        if (relativeDir is "." or "" or null)
        {
            return new(fullPath, fileName, normalizedRoot, Array.Empty<string>());
        }

        // 防 "..": 文件其实不在监控根之下（路径父集关系反转）→ 不暴露监控根外的段
        if (relativeDir.StartsWith("..", StringComparison.Ordinal))
        {
            return FromFullPath(fullPath, null);
        }

        IReadOnlyList<string> segments = relativeDir
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        return new(fullPath, fileName, normalizedRoot, segments);
    }

    private static string NormalizeDirPath(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
