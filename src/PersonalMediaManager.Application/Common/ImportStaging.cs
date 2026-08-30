namespace PersonalMediaManager.Application.Common;

/// <summary>导入包「暂存 + 启动期换库」协议</summary>
/// <remarks>
/// 为什么不能在运行中直接覆盖 pmm.db：SQLite 跑在 WAL 模式（journal_mode=WAL），运行中的连接持有
/// pmm.db-wal / pmm.db-shm 与页缓存；进程内 File.Copy 覆盖主库后，旧 WAL 会在 checkpoint / 进程关闭时
/// 回写覆盖刚导入的内容 —— 表现为「导入没作用」（重启反而坐实回退）。
///
/// 正确做法（本类型实现）：
/// 1. 导入时只把新库写到 &lt;db&gt;.import-pending（绝不碰运行中的主库）；
/// 2. 下次进程启动、任何 DbContext 连接打开之前，调用 <see cref="ApplyPendingIfAny"/> 原子换库：
///    备份现库 → 删主库 + 残留 -wal/-shm → 把 pending 改名为主库。
/// 换库窗口位于「无任何连接打开」时刻，从根上杜绝 WAL 回写竞争。
/// </remarks>
public static class ImportStaging
{
    /// <summary>暂存文件后缀（追加在主库路径后）</summary>
    public const string PendingSuffix = ".import-pending";

    // SQLite 库头 16 字节魔数 "SQLite format 3\0"
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>主库对应的暂存文件路径</summary>
    public static string PendingPathFor(string dbPath) => dbPath + PendingSuffix;

    /// <summary>粗判文件是否为合法 SQLite 库（头 16 字节魔数 + 最小尺寸），防把坏包/空文件换上去抹掉现库</summary>
    public static bool IsLikelySqlite(string path)
    {
        try
        {
            FileInfo fi = new(path);
            if (!fi.Exists || fi.Length < 512) return false;
            using FileStream fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            return fs.Read(head) == 16 && head.SequenceEqual(SqliteMagic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>若存在待生效的导入暂存包，则在开库前原子换库。必须在任何 SQLite 连接打开之前调用</summary>
    /// <param name="dbPath">运行时生效的主库绝对路径（override 感知，取自 <see cref="DatabaseFileLocation"/>）</param>
    /// <param name="log">可选日志回调（中文）</param>
    /// <returns>true=本次完成了换库；false=无暂存包或换库未执行</returns>
    public static bool ApplyPendingIfAny(string dbPath, Action<string>? log = null)
    {
        string pending = PendingPathFor(dbPath);
        if (!File.Exists(pending)) return false;

        // 防御：暂存包必须是合法 SQLite，否则挪到 .invalid 并放弃（绝不拿坏包覆盖现库）
        if (!IsLikelySqlite(pending))
        {
            string invalid = pending + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try { File.Move(pending, invalid); } catch { /* best-effort */ }
            log?.Invoke($"导入暂存包非法（非 SQLite 文件），已弃置为 {invalid}，本次不换库");
            return false;
        }

        try
        {
            // 1. 备份现库（存在才备份）。崩溃安全：pending 在改名前始终是权威源，任一步崩溃后下次启动可重做
            if (File.Exists(dbPath))
            {
                string backup = dbPath + $".preimport-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                File.Copy(dbPath, backup, overwrite: false);
                log?.Invoke($"导入换库：已备份现库 → {backup}");
            }

            // 2. 删主库 + 残留 WAL/SHM —— 关键：杜绝旧 WAL 在新库上被 checkpoint 回写
            DeleteIfExists(dbPath);
            DeleteIfExists(dbPath + "-wal");
            DeleteIfExists(dbPath + "-shm");

            // 3. 暂存包就位（改名 = 原子）
            File.Move(pending, dbPath);
            log?.Invoke($"导入换库完成：{pending} → {dbPath}");
            return true;
        }
        catch (Exception ex)
        {
            // 换库失败不能阻断启动：保留 pending（下次纯净启动重试）；
            // 多为「进程内重启」旧连接仍占用文件句柄所致，全新进程启动时句柄已释放可成功
            log?.Invoke($"导入换库失败（已保留暂存包待下次启动重试）：{ex.Message}");
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
