// Launcher 入口（详见 docs/需求规范-启动方式与托盘常驻.md §1.1）
//
// 设计要点：
// 1. [STAThread] 必须 —— WinForms / Shell COM API（剪贴板、SaveFileDialog 等）要求 STA
// 2. ApplicationConfiguration.Initialize() 统一接管 EnableVisualStyles + SetCompatibleTextRenderingDefault + 高 DPI
// 3. 单实例守护放在 Main 而不是 PmmTrayContext —— 非主实例不应进入消息循环，发完 IPC OPEN 立即退出
// 4. Application.Run(context) 阻塞主线程直到 PmmTrayContext.ExitThread()；context 释放后落到 Main 收尾
// 5. 启动期 try/catch + tray-crash.log 兜底属于 E1.12，本次（E1.10）不增

#if PMM_WINDOWS
using System.Windows.Forms;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Launcher;
using PersonalMediaManager.Launcher.Platform;
using PersonalMediaManager.Launcher.Platform.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 启动期致命错误兜底（规范 §1.3 §9）：
        // 所有从 ApplicationConfiguration.Initialize 到 Application.Run 期间的异常都被 catch；
        // 异常 → tray-crash.log + MessageBox + 非零 exit code；写文件 / MessageBox 失败再吞不升级（crash 兜底自己再 crash 是最差体验）
        AppPaths? paths = null;
        IPlatformSingleInstance? singleInstance = null;
        try
        {
            ApplicationConfiguration.Initialize();
            paths = AppPaths.Resolve();

            singleInstance = new WindowsSingleInstance();
            if (!singleInstance.TryAcquire())
            {
                // 已有主实例：发 IPC OPEN 让其弹浏览器，本进程立刻退出（finally 会 Dispose singleInstance）
                singleInstance.NotifyExistingInstance();
                return 0;
            }

            using PmmTrayContext context = new(args, paths, singleInstance);
            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            HandleFatalStartupError(ex, paths);
            return 1;
        }
        finally
        {
            // singleInstance 在 Main 创建，由 Main 兜底释放；幂等：非主实例路径已 NotifyExistingInstance 但 Dispose 由这里统一管
            singleInstance?.Dispose();
            // PmmHost.CreateApp 已配 Serilog 全局 Logger；flush 让 crash stack trace 入文件，未配置时是 no-op
            try { Serilog.Log.CloseAndFlush(); } catch { /* 静默 */ }
        }
    }

    /// <summary>启动期异常兜底：写 tray-crash.log + 弹 MessageBox（全程不再升级异常）</summary>
    private static void HandleFatalStartupError(Exception ex, AppPaths? paths)
    {
        // 疑似数据库损坏时附带中文恢复指引（从 backups/ 恢复 + 指向恢复文档），降低用户惊慌
        string hint = BuildCorruptionHintIfAny(ex, paths);
        string hintBlock = hint.Length == 0 ? string.Empty : hint + Environment.NewLine + Environment.NewLine;
        string crashText = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] PMM Launcher 启动失败：{Environment.NewLine}{hintBlock}{ex}{Environment.NewLine}{Environment.NewLine}";

        // crash log：优先 AppPaths.Root；AppPaths 解析前就崩则落 BaseDirectory 兜底
        try
        {
            string crashLogPath = paths is not null
                ? Path.Combine(paths.Root, "tray-crash.log")
                : Path.Combine(AppContext.BaseDirectory, "tray-crash.log");
            File.AppendAllText(crashLogPath, crashText);
        }
        catch
        {
            // 写文件失败不再升级（磁盘满 / 权限不足 / 路径锁定）
        }

        // MessageBox：Session 0 / 无桌面环境弹不出来；包 try-catch 避免兜底再 crash
        // 恢复指引放在异常详情之前，确保用户先看到可操作的建议
        try
        {
            string boxText = hint.Length == 0 ? ex.ToString() : hint + Environment.NewLine + Environment.NewLine + ex;
            MessageBox.Show(
                boxText,
                "PersonalMediaManager 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 无桌面环境（如 Session 0 服务进程意外被当成 Launcher 拉起）
        }
    }

    /// <summary>疑似 SQLite 数据库损坏时返回面向用户的中文恢复指引（否则返空串）</summary>
    /// <remarks>沿异常链匹配 SQLite 损坏关键字；命中则指引用户从 backups/ 最近备份恢复，并指向 docs/数据库损坏恢复.md。</remarks>
    private static string BuildCorruptionHintIfAny(Exception ex, AppPaths? paths)
    {
        bool looksCorrupt = false;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            string m = e.Message;
            if (m.Contains("malformed", StringComparison.OrdinalIgnoreCase)
                || m.Contains("not a database", StringComparison.OrdinalIgnoreCase)
                || m.Contains("disk image", StringComparison.OrdinalIgnoreCase)
                || m.Contains("SQLITE_CORRUPT", StringComparison.OrdinalIgnoreCase))
            {
                looksCorrupt = true;
                break;
            }
        }
        if (!looksCorrupt) return string.Empty;

        string dataDir = paths is not null ? paths.Root : "（数据目录，通常在 %LocalAppData%\\PersonalMediaManager 或程序目录\\data）";
        return "⚠ 检测到数据库可能已损坏。恢复办法（应用已无法启动，需手动操作）：" + Environment.NewLine
            + "  1) 完整退出本应用；" + Environment.NewLine
            + $"  2) 打开数据目录：{dataDir}" + Environment.NewLine
            + "  3) 进入 backups\\ 找到最近的 pmm-backup-*.zip；" + Environment.NewLine
            + "  4) 按 docs\\数据库损坏恢复.md 用该备份替换损坏的 pmm.db（含密钥环 keys\\）后重启。" + Environment.NewLine
            + "  若无可用备份，文档也给出 sqlite3 .recover 深度恢复步骤。";
    }
}
#endif
