using System.Runtime.InteropServices;

namespace PersonalMediaManager.Application.Common;

/// <summary>应用路径解析（需求文档 §3.11 / 4.3）</summary>
/// <remarks>
/// Windows: 优先「程序目录\data\」（绿色软件：数据跟随 exe，整目录可备份/迁移/卸载）；
///          程序目录不可写（典型：安装到 C:\Program Files\）时回退 %LocalAppData%\PersonalMediaManager。
/// 非 Windows: ~/.local/share/PersonalMediaManager（产品仅支持 Windows，此分支仅作兜底不构成支持承诺）
/// 子目录：db / logs / keys / cache/posters。Resolve() 时自动 mkdir。
/// </remarks>
public sealed class AppPaths
{
    public string Root { get; }
    public string DbFile => Path.Combine(Root, "pmm.db");
    public string LogDir => Path.Combine(Root, "logs");
    public string KeyRingDir => Path.Combine(Root, "keys");
    public string CacheDir => Path.Combine(Root, "cache");
    public string PostersDir => Path.Combine(CacheDir, "posters");
    /// <summary>自动备份落点（pmm-backup-*.zip 在线快照，含 pmm.db + 密钥环 keys/*，与数据根同根便于一锅端迁移）</summary>
    public string BackupDir => Path.Combine(Root, "backups");

    private AppPaths(string root)
    {
        Root = root;
    }

    public static AppPaths Resolve()
    {
        string root = ResolveRoot();
        Directory.CreateDirectory(root);
        TryMigrateLegacyProgramData(root); // 摘除提权后：旧版「数据贴 Program Files\exe」的 data 目录一次性迁回新根
        AppPaths paths = new(root);
        Directory.CreateDirectory(paths.LogDir);
        Directory.CreateDirectory(paths.KeyRingDir);
        Directory.CreateDirectory(paths.PostersDir);
        Directory.CreateDirectory(paths.BackupDir);
        return paths;
    }

    /// <summary>测试可显式指定根目录（避免污染用户 AppData）</summary>
    public static AppPaths ForRoot(string root)
    {
        Directory.CreateDirectory(root);
        AppPaths paths = new(root);
        Directory.CreateDirectory(paths.LogDir);
        Directory.CreateDirectory(paths.KeyRingDir);
        Directory.CreateDirectory(paths.PostersDir);
        Directory.CreateDirectory(paths.BackupDir);
        return paths;
    }

    private static string ResolveRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 优先「程序目录\data\」（绿色软件：数据跟随 exe）；不可写则回退用户 %LocalAppData%
            string? programDataRoot = TryResolveProgramDataRoot();
            if (programDataRoot is not null)
            {
                return programDataRoot;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "PersonalMediaManager");
        }

        // 非 Windows 兜底（产品仅支持 Windows，不构成支持承诺）
        string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdgData, "PersonalMediaManager");
    }

    /// <summary>解析「程序目录\data\」并校验可写；exe 路径不可得或目录不可写时返 null（交调用方回退 %LocalAppData%）</summary>
    /// <remarks>
    /// 必须用 Environment.ProcessPath 取 exe 真实所在目录：单文件发布下 AppContext.BaseDirectory 指向
    /// %TEMP%\.net\... 临时解压目录（每次升级 hash 变、只读语义），用它做数据基准会导致 db 升级即丢。
    /// CreateDirectory 在 Program Files 等位置可能"成功"但真正写文件被 UAC 虚拟化/拒绝，故额外写探针文件做真实可写性校验。
    /// </remarks>
    private static string? TryResolveProgramDataRoot()
    {
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(exeDir))
        {
            return null;
        }

        string dataRoot = Path.Combine(exeDir, "data");
        try
        {
            Directory.CreateDirectory(dataRoot);
            string probe = Path.Combine(dataRoot, $".pmm-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return dataRoot;
        }
        catch
        {
            // 程序目录不可写（权限不足 / 只读介质 / UAC）→ 交回退
            return null;
        }
    }

    /// <summary>一次性数据迁移：摘除提权后，把旧版提权安装「贴 Program Files\exe」的 data 目录迁回新根（%LocalAppData%）</summary>
    /// <remarks>
    /// 触发条件（全满足才迁）：Windows + 本次已回退到非 exe 目录（典型 %LocalAppData%）+ 新根尚无 pmm.db + 旧 {exeDir}\data\pmm.db 存在。
    /// 整目录递归复制：含 keys（DataProtection 密钥不一起搬，TMDB/AI/PAT 等加密字段解不开）、含 pmm.db-wal/-shm
    /// （与 db 同组复制，规避「外来 WAL 孤儿」损坏）。已存在的目标文件跳过不覆盖。
    /// 失败静默吞：旧库原地保留不丢，最坏是在新根重新初始化；成功留一张 .migrated-from.txt 面包屑供事后核对。
    /// </remarks>
    private static void TryMigrateLegacyProgramData(string newRoot)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(exeDir))
        {
            return;
        }
        string legacyRoot = Path.Combine(exeDir, "data");
        // 没回退（数据本就贴 exe）/ 新根已有库 / 旧根无库 → 都不迁
        if (string.Equals(legacyRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(Path.Combine(newRoot, "pmm.db"))) return;
        if (!File.Exists(Path.Combine(legacyRoot, "pmm.db"))) return;

        try
        {
            CopyDirectoryRecursive(legacyRoot, newRoot);
            File.WriteAllText(
                Path.Combine(newRoot, ".migrated-from.txt"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} 自旧提权安装数据目录迁入：{legacyRoot}{Environment.NewLine}");
        }
        catch
        {
            // 迁移失败不阻断启动：旧库文件原地保留不丢
        }
    }

    /// <summary>递归复制目录；已存在的目标文件跳过不覆盖（保护新根已写入的内容）</summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string dest = Path.Combine(destDir, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                File.Copy(file, dest);
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
