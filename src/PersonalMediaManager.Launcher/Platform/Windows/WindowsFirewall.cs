#if PMM_WINDOWS
using System.Diagnostics;

namespace PersonalMediaManager.Launcher.Platform.Windows;

/// <summary>Windows 防火墙规则注入（首启检测 + UAC 提权调用 netsh）</summary>
/// <remarks>
/// 设计要点：
/// - 仅注册入站 TCP 7288（默认端口；可由 appsettings.Web:Port 覆盖）
/// - 规则名 "PersonalMediaManager Web UI"；检测先用 netsh advfirewall firewall show rule name=...
/// - 添加用 New-NetFirewallRule（PowerShell）+ Verb=runas 触发 UAC
/// - UAC 拒绝（user cancel / 非管理员账户）→ 降级到调用方传入的 onUacDenied 回调（典型：托盘气泡提示）
/// - 任何异常都不阻断启动流程（防火墙未注入只影响局域网访问，本机 localhost 仍能用）
/// </remarks>
public sealed class WindowsFirewall
{
    private const string RuleName = "PersonalMediaManager Web UI";

    /// <summary>确保规则存在；不存在则尝试注入。返回是否最终存在。</summary>
    /// <param name="port">入站 TCP 端口</param>
    /// <param name="onUacDenied">UAC 失败时的降级回调（如托盘气泡提示）</param>
    public bool EnsureRule(int port, Action<string>? onUacDenied = null)
    {
        if (RuleExists()) return true;

        try
        {
            AddRuleElevated(port);
            return RuleExists();
        }
        catch (Exception ex)
        {
            onUacDenied?.Invoke($"防火墙规则注入失败（{ex.Message}）；请手动允许 TCP {port} 入站，或本机访问 http://localhost:{port}");
            return false;
        }
    }

    private static bool RuleExists()
    {
        ProcessStartInfo psi = new("netsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("advfirewall");
        psi.ArgumentList.Add("firewall");
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add("rule");
        psi.ArgumentList.Add($"name={RuleName}");

        try
        {
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("netsh 启动失败");
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AddRuleElevated(int port)
    {
        // PowerShell -Command 用 New-NetFirewallRule（比 netsh advfirewall firewall add rule 更稳）
        string psCommand = $"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -Profile Any";

        ProcessStartInfo psi = new("powershell")
        {
            UseShellExecute = true, // Verb=runas 要求 UseShellExecute=true
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(psCommand);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell 启动失败（UAC 可能被拒绝）");
        p.WaitForExit(10000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"New-NetFirewallRule 退出码 {p.ExitCode}");
        }
    }
}
#endif
