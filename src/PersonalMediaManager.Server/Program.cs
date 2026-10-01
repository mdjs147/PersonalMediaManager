using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Composition;
using PersonalMediaManager.Server;

try
{
    ServerOptions options = ServerOptions.Parse(args);
    if (options.Help)
    {
        Console.WriteLine("PersonalMediaManager.Server [--data-dir 绝对路径] [--port 7288] [--bind 127.0.0.1] [--headless]\nmacOS 默认显示原生菜单栏；--headless 用于无桌面服务。Ctrl+C / SIGTERM 安全退出。");
        return 0;
    }
    AppPaths paths = options.DataDirectory is null ? AppPaths.Resolve() : AppPaths.ForRoot(options.DataDirectory);
    // 锁文件不能在退出时删除：删除会让并发进程锁住不同 inode。
    using DataRootLease instance = new(paths.Root);
    await using WebApplication app = PmmHost.CreateApp(options.ConfigurationArgs, paths, portableServer: true);
    string bind = app.Configuration["Web:BindAddress"] ?? "127.0.0.1";
    if (OperatingSystem.IsMacOS() && !options.Headless && bind is not ("127.0.0.1" or "0.0.0.0"))
        throw new ArgumentException("macOS 菜单栏模式需要绑定 127.0.0.1 或 0.0.0.0；其他地址请使用 --headless");
    await PmmBootstrap.StartAsync(app);
    Console.WriteLine($"数据目录：{paths.Root}");
    int port = app.Configuration.GetValue("Web:Port", PmmHost.DefaultPort);
    Console.WriteLine($"WebUI：http://127.0.0.1:{port}/");
    try
    {
        using MacTrayProcess? tray = OperatingSystem.IsMacOS() && !options.Headless
            ? MacTrayProcess.Start(port, app.Lifetime) : null;
        await app.WaitForShutdownAsync();
    }
    finally { await app.StopAsync(); }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"启动或运行失败：{ex.Message}");
    return 1;
}
