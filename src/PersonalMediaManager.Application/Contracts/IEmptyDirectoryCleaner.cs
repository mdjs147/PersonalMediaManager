namespace PersonalMediaManager.Application.Contracts;

/// <summary>源端空目录清理契约 — 归档移走文件后向上回收空 / 仅含可忽略残留的源目录</summary>
/// <remarks>
/// 实现位于 Infrastructure.Platform/FileSystem/EmptyDirectoryCleaner.cs。
/// 「可视为空」判定：目录内除 ignoreExtensions 列出的扩展名外无其它文件，且所有子目录递归满足同一条件；
/// ignoreExtensions 为空集 = 严格模式（仅真正的空目录可删）。逐项删除残留后非递归删除目录，保留新到达文件。
/// 上溯边界：从 startDirectory 逐层向父目录回收，到 boundary 处停止 —— 绝不删除 boundary 本身（监控根）。
/// 纯文件系统操作，不读设置；开关与清单由调用方（ArchiveService）读 System_Setting 后传入。
/// </remarks>
public interface IEmptyDirectoryCleaner
{
    /// <summary>从 startDirectory 向上回收空目录，到 boundary 为止（不含 boundary）</summary>
    /// <param name="startDirectory">起点目录（通常是被归档源文件所在目录）</param>
    /// <param name="boundary">回收下界（监控根目录）；startDirectory 必须严格位于其下，否则不做任何清理</param>
    /// <param name="ignoreExtensions">可忽略扩展名（含前导点、大小写不敏感）；命中这些的残留文件不计入「非空」</param>
    /// <param name="ct">取消令牌（逐层回收前检查）</param>
    /// <returns>实际删除的目录绝对路径列表（供日志）</returns>
    IReadOnlyList<string> CleanUpward(
        string startDirectory,
        string boundary,
        IReadOnlySet<string> ignoreExtensions,
        CancellationToken ct = default);

    /// <summary>每次删除子树前异步复核业务条件，再重验文件系统边界与内容</summary>
    /// <remarks>canDeleteDirectory 检查整个候选子树；返回 false 即停止上溯，不能借由祖先递归删除受保护目录。</remarks>
    Task<IReadOnlyList<string>> CleanUpwardAsync(
        string startDirectory,
        string boundary,
        IReadOnlySet<string> ignoreExtensions,
        Func<string, CancellationToken, Task<bool>> canDeleteDirectory,
        CancellationToken ct = default);
}
