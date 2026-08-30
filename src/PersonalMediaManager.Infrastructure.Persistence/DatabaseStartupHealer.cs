using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PersonalMediaManager.Infrastructure.Persistence;

/// <summary>启动期数据库自愈：预防性 checkpoint + malformed 误报捕获 + 单次重试</summary>
/// <remarks>
/// 背景：进程异常退出（强杀 / 崩溃 / 断电）会留下未 checkpoint 的 `-wal` 文件，
/// 下一次 EF Core 在 SqliteHistoryRepository.AcquireDatabaseLock() 拿迁移锁时，
/// SQLite 偶发对残留 WAL + 主库交叉读时误报 SQLITE_CORRUPT (errcode=11, "database disk image is malformed")，
/// 但 PRAGMA integrity_check 实际返回 ok。
///
/// 自愈策略（按序）：
/// 1. 预防：执行 `PRAGMA wal_checkpoint(TRUNCATE)` 把残留 WAL 合并回主库（无 WAL 时 noop，异常吞掉记 warn）
/// 2. 执行：ctx.Database.Migrate()
/// 3. 兜底：捕 SqliteException(errcode=11) → 跑 integrity_check：
///    · 返回 "ok" → 再 checkpoint 一次 → 重试 Migrate（仅一次）
///    · 返回非 ok → 判定真损坏，原样重抛让上层弹错（保留备份 / 提示重装）
///
/// 调用方：Launcher.PmmTrayContext 启动期 Migrate；测试夹具也可借此验证迁移路径。
/// 仅捕获已知 false-positive 错码，其他 SqliteException / InvalidOperationException 一律直抛。
/// </remarks>
public static class DatabaseStartupHealer
{
    /// <summary>SQLite errcode = 11 (SQLITE_CORRUPT)：消息形如 "database disk image is malformed"</summary>
    private const int SqliteErrorCorrupt = 11;

    /// <summary>启动期 Migrate 入口：预 checkpoint + Migrate + 单次自愈重试</summary>
    public static void MigrateWithSelfHeal(PmmDbContext ctx, ILogger? logger = null)
        => MigrateWithSelfHealCore(ctx, () => ctx.Database.Migrate(), logger ?? NullLogger.Instance);

    /// <summary>测试可注入的内部入口：用 migrate 委托替代真实 EF Migrate，便于伪造 SqliteException 验证控制流</summary>
    internal static void MigrateWithSelfHealCore(PmmDbContext ctx, Action migrate, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentNullException.ThrowIfNull(logger);

        TryCheckpoint(ctx, logger, "Migrate 前预防性 checkpoint");

        try
        {
            migrate();
            return;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteErrorCorrupt)
        {
            logger.LogWarning(ex,
                "Migrate 触发 SQLITE_CORRUPT (errcode={ErrCode})，疑似残留 WAL 误报，尝试自愈：integrity_check + 重试",
                ex.SqliteErrorCode);

            string integrity = ReadIntegrityCheck(ctx);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "integrity_check 返回 '{Integrity}'（非 ok），判定为真损坏，向上抛出原异常",
                    integrity);
                throw;
            }

            TryCheckpoint(ctx, logger, "自愈期 checkpoint");
            logger.LogInformation("integrity_check=ok 且 WAL 已合并，进入一次性 Migrate 重试");
        }

        // 一次性重试：仍失败则向上抛（不再二次自愈，避免无限循环）
        migrate();
    }

    /// <summary>跑 PRAGMA wal_checkpoint(TRUNCATE)。失败仅记 warn，不影响后续 Migrate</summary>
    private static void TryCheckpoint(PmmDbContext ctx, ILogger logger, string label)
    {
        try
        {
            // ExecuteSqlRaw 走 EF 的 RelationalCommand，自动复用 DbContext 连接
            ctx.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (Exception ex)
        {
            // checkpoint 失败不致命（Migrate 仍可能成功 / 即便失败也会进入自愈分支）
            logger.LogWarning(ex, "{Label} 失败：{Message}", label, ex.Message);
        }
    }

    /// <summary>读 PRAGMA quick_check 第一行；"ok" 表示无损坏</summary>
    /// <remarks>
    /// 用 quick_check 而非 integrity_check：前者跳过 UNIQUE/CHECK 等索引验证，速度更快，
    /// 对「主库物理损坏 vs WAL 残留误报」的二分判断足够。
    /// 直接走 DbConnection.CreateCommand 避免 EF 的 SqlQueryRaw 把单标量结果包装成实体类型时的边界问题。
    /// </remarks>
    private static string ReadIntegrityCheck(PmmDbContext ctx)
    {
        try
        {
            System.Data.Common.DbConnection conn = ctx.Database.GetDbConnection();
            bool wasClosed = conn.State != System.Data.ConnectionState.Open;
            if (wasClosed) conn.Open();
            try
            {
                using System.Data.Common.DbCommand cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check";
                object? result = cmd.ExecuteScalar();
                return result?.ToString() ?? "<null>";
            }
            finally
            {
                if (wasClosed) conn.Close();
            }
        }
        catch (Exception ex)
        {
            return $"<read-failed: {ex.GetType().Name}: {ex.Message}>";
        }
    }
}
