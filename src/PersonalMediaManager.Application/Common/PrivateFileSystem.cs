namespace PersonalMediaManager.Application.Common;

/// <summary>应用私有目录与敏感文件的权限边界</summary>
/// <remarks>Unix 新目录为 0700，新文件为 0600。已有目录权限不合规时拒绝继续，不修改用户目录权限。</remarks>
public static class PrivateFileSystem
{
    public const UnixFileMode DirectoryPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SharedPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>创建私有目录；拒绝符号链接与已存在的开放目录</summary>
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        RejectSymbolicLink(path);
        Directory.CreateDirectory(path, DirectoryPermissions);
        RejectSymbolicLink(path);
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & SharedPermissions) != 0 || (mode & DirectoryPermissions) != DirectoryPermissions)
        {
            throw new UnauthorizedAccessException($"数据目录必须仅允许当前用户访问（权限 0700）：{path}。请自行检查目录归属并调整权限，或指定一个新的数据目录。");
        }
    }

    /// <summary>创建隔离的私有临时目录</summary>
    public static string CreateTemporaryDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        EnsureDirectory(path);
        return path;
    }

    /// <summary>独占创建敏感文件，创建瞬间即限制 Unix 权限</summary>
    public static FileStream CreateNew(string path)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FilePermissions;
        return new FileStream(path, options);
    }

    /// <summary>限制应用已拥有的敏感文件权限，不允许跟随符号链接</summary>
    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        RejectSymbolicLink(path);
        File.SetUnixFileMode(path, FilePermissions);
    }

    /// <summary>通过私有临时文件发布完整副本，避免暴露半写入的敏感文件</summary>
    public static void CopyFile(string source, string destination, bool overwrite)
    {
        RejectSymbolicLink(source);
        RejectSymbolicLink(destination);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream output = CreateNew(temporary))
            using (FileStream input = File.OpenRead(source))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>拒绝应用敏感落点的符号链接</summary>
    public static void RejectSymbolicLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new UnauthorizedAccessException($"敏感数据路径不能是符号链接：{path}");
    }
}
