#if PMM_WINDOWS
using Microsoft.Win32;

namespace PersonalMediaManager.Launcher.Platform.Windows;

/// <summary>Windows 开机自启实现（HKCU\Run 注册表值，普通权限登录即启）</summary>
/// <remarks>
/// 为何用 HKCU\Run 而非计划任务：
///   exe 已改 asInvoker（普通权限）运行。HKCU\Run 在登录时即以普通完整性级别静默启动、不弹 UAC、
///   跑在交互会话（有托盘 UI），是常驻托盘程序最简洁的自启方式，且与「普通权限 → 共享会话网络盘映射」目标一致。
/// 实现：写 HKCU\...\Run 值 = "{exe}" --autostart；零 shell-out。
/// 幂等：Enable 覆盖写；Disable 删不存在的值静默吞；IsEnabled = Run 值存在。
/// 权限：HKCU 是当前用户配置单元，读写无需管理员。
/// </remarks>
public sealed class WindowsAutoStart : IPlatformAutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Run 值名</summary>
    private const string EntryName = "PersonalMediaManager";

    private readonly string _entryName;

    /// <summary>生产构造：Run 值名 "PersonalMediaManager"</summary>
    public WindowsAutoStart() : this(EntryName) { }

    /// <summary>测试构造：注入唯一名避免污染真实自启项</summary>
    public WindowsAutoStart(string entryName)
    {
        _entryName = entryName;
    }

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_entryName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public void Enable()
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前进程可执行路径（Environment.ProcessPath 为 null）");

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($@"无法打开注册表项 HKCU\{RunKeyPath}");
        key.SetValue(_entryName, BuildRunCommand(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(_entryName, throwOnMissingValue: false);
        }
        catch
        {
            // 删 Run 值失败静默吞（幂等：值不存在 / 偶发 IO）
        }
    }

    /// <summary>构造 HKCU\Run 值内容（纯函数，便于单测）：带引号的 exe 路径 + --autostart 参数</summary>
    /// <remarks>引号包裹 exe 路径以容纳含空格的安装目录（如 C:\Program Files\...）；--autostart 让自启时不弹启动气泡。</remarks>
    public static string BuildRunCommand(string exePath) => $"\"{exePath}\" --autostart";
}
#endif
