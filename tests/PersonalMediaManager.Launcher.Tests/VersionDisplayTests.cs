#if PMM_WINDOWS
using System.Reflection;
using PersonalMediaManager.Launcher.Platform.Windows;

namespace PersonalMediaManager.Launcher.Tests;

/// <summary>托盘离线版本展示保持唯一主版本</summary>
public sealed class VersionDisplayTests
{
    [Fact(DisplayName = "托盘关于只显示主版本与构建诊断，不列前后端和数据库独立版本")]
    public void AboutText_ShowsProductAndBuildDiagnostics()
    {
        Assembly assembly = typeof(WindowsAutoStart).Assembly;
        Type trayType = assembly.GetType("PersonalMediaManager.Launcher.PmmTrayContext", throwOnError: true)!;
        string product = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ProductVersion").Value!;
        string body = (string)trayType.GetMethod("BuildAboutText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        body.Should().StartWith($"PersonalMediaManager v{product}\r\n")
            .And.Contain("提交：")
            .And.Contain("构建时间：")
            .And.NotContain("后端：")
            .And.NotContain("前端：")
            .And.NotContain("数据库目标：");
    }
}
#endif
