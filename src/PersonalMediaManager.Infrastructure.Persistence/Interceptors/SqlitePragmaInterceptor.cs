using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PersonalMediaManager.Infrastructure.Persistence.Interceptors;

/// <summary>连接拦截器：每条 SQLite 连接打开后统一应用并发相关 PRAGMA</summary>
/// <remarks>
/// 解决「database is locked」(SQLITE_BUSY)：默认连接串只有 Data Source，既没开 WAL 也没设 busy_timeout，
/// SQLite 默认 journal_mode=DELETE（写事务独占、读写互相阻塞）且撞锁立即抛错不等待。
/// 后台 Worker 持写锁时，前端开新 DbContext 写库会瞬间撞锁失败。
///
/// PRAGMA 含义（与 docs/数据库设计.md §726-730 对齐）：
/// - busy_timeout=5000：撞锁后最多自旋等待 5s 再抛错（**每连接**生效，必须每次打开都设）；先设它，保证后续 PRAGMA 自身也走等待重试。
/// - journal_mode=WAL：读不阻塞写、写不阻塞读，仅写写互斥，根除「读连接卡住写连接」的同进程死锁；WAL 是库级持久设置，重复设幂等。
/// - synchronous=NORMAL：WAL 下安全且更快（崩溃至多丢最后一次未 checkpoint 的事务，不损坏库）。
/// - foreign_keys=ON：SQLite 外键默认关，且**每连接**生效，必须每次打开都设。
/// - temp_store=MEMORY：临时表/索引走内存。
///
/// 注：DbContextFactory 创建的每条连接都会触发本拦截器，故无状态、注册为单例。
/// </remarks>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string PragmaBatch =
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA foreign_keys=ON;" +
        "PRAGMA temp_store=MEMORY;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = PragmaBatch;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = PragmaBatch;
        cmd.ExecuteNonQuery();
    }
}
