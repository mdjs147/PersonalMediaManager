#if PMM_WINDOWS
using PersonalMediaManager.Launcher.Platform.Windows;

namespace PersonalMediaManager.Launcher.Tests.Windows;

/// <summary>Windows 开机自启 HKCU\Run 值构造测试</summary>
/// <remarks>
/// 只测纯函数 BuildRunCommand（不触真实注册表 / 计划任务、无需管理员），与 WindowsFirewall 的 shell-out 不做真实执行单测一致。
/// 真实写 HKCU\Run / 删旧计划任务会污染当前用户自启项，留作本地手动 / 集成验证。
/// </remarks>
public sealed class WindowsAutoStartTests
{
    [Fact(DisplayName = "Run 值 = 引号包裹 exe 路径 + --autostart 参数")]
    public void BuildRunCommand_QuotesExeAndAppendsAutostart()
    {
        string cmd = WindowsAutoStart.BuildRunCommand(@"C:\Program Files\PMM\PersonalMediaManager.exe");

        cmd.Should().Be(@"""C:\Program Files\PMM\PersonalMediaManager.exe"" --autostart");
    }

    [Fact(DisplayName = "含空格安装目录的 exe 路径被引号包裹，Run 解析不被空格截断")]
    public void BuildRunCommand_PathWithSpacesIsQuoted()
    {
        string cmd = WindowsAutoStart.BuildRunCommand(@"D:\Program Files\PersonalMediaManager\PersonalMediaManager.exe");

        // 起始引号 + 路径 + 结束引号，确保 "D:\Program Files\..." 被当作单一可执行而非 "D:\Program" + 参数 "Files\..."
        cmd.Should().StartWith("\"");
        cmd.Should().Contain("\"D:\\Program Files\\PersonalMediaManager\\PersonalMediaManager.exe\"");
        cmd.Should().EndWith(" --autostart");
    }
}
#endif
