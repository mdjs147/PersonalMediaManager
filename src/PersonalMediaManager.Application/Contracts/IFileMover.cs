namespace PersonalMediaManager.Application.Contracts;

/// <summary>文件移动 / 复制契约（D4.1）— 归档落地的最底层动作</summary>
/// <remarks>
/// 实现位于 Infrastructure.Platform/FileSystem/FileMover.cs：
/// - 模式二选一：Move 或 Copy（用户在分类配置里指定，原盘有限可走 Move 节省空间；网盘/NAS 走 Copy 保留源文件）
/// - 同名冲突：目标已存在 → 抛 FileConflictException（调用方决定覆盖 / 跳过 / 重命名）
/// - 跨卷 Move：File.Move 失败时 fallback 到 Copy + Delete 源（Windows 不同盘符 / Linux 不同 mount point）
/// - 目标目录不存在则递归创建（File.Move 不会自动建目录）
/// - 操作必须原子可见：Copy 时先写入 .{filename}.tmp，写完 rename 到最终名（避免归档过程被 Plex 扫到半成品）
/// </remarks>
public interface IFileMover
{
    /// <summary>移动文件（同卷优先 rename，跨卷 fallback Copy+Delete）</summary>
    /// <exception cref="FileConflictException">目标路径已存在</exception>
    /// <exception cref="FileNotFoundException">源文件不存在</exception>
    Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default);

    /// <summary>复制文件（永远 Copy；源文件保留）</summary>
    /// <exception cref="FileConflictException">目标路径已存在</exception>
    /// <exception cref="FileNotFoundException">源文件不存在</exception>
    Task CopyAsync(string sourcePath, string targetPath, CancellationToken ct = default);
}

/// <summary>归档目标已存在（不覆盖）— 调用方拿到后决定 Skip / Rename / 报错</summary>
public sealed class FileConflictException : Exception
{
    public FileConflictException(string targetPath)
        : base($"目标路径已存在：{targetPath}")
    {
        TargetPath = targetPath;
    }

    public string TargetPath { get; }
}
