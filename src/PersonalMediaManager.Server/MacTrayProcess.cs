using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace PersonalMediaManager.Server;

/// <summary>原生菜单栏辅助进程与服务生命周期桥接</summary>
internal sealed class MacTrayProcess : IDisposable
{
    private readonly Process _process;
    private readonly IDisposable _stopping;

    private MacTrayProcess(Process process, IHostApplicationLifetime lifetime)
    {
        _process = process;
        _stopping = lifetime.ApplicationStopping.Register(() =>
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        });
        _ = ObserveAsync(lifetime);
    }

    public static MacTrayProcess Start(int port, IHostApplicationLifetime lifetime)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "PersonalMediaManager.MacTray");
        if (!File.Exists(helper))
            throw new FileNotFoundException("缺少 macOS 菜单栏组件，请使用 macOS 打包脚本；无桌面服务请显式加 --headless", helper);
        ProcessStartInfo info = new(helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add($"http://127.0.0.1:{port}/");
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new(Process.Start(info) ?? throw new InvalidOperationException("无法启动 macOS 菜单栏"), lifetime);
    }

    private async Task ObserveAsync(IHostApplicationLifetime lifetime)
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line == "quit") { lifetime.StopApplication(); return; }
            }
            await _process.WaitForExitAsync();
            if (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                Console.Error.WriteLine("macOS 菜单栏已退出，服务将安全停止");
                lifetime.StopApplication();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (!lifetime.ApplicationStopping.IsCancellationRequested) lifetime.StopApplication();
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(3000)) _process.Kill();
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
