using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Host.Composition;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests;

/// <summary>WebApplicationFactory：使用 TestServer 替换 Kestrel，独立临时 AppPaths 避免污染用户目录</summary>
/// <remarks>
/// Launcher 是 Exe + 顶级语句 + 同步主入口，无法供 WebApplicationFactory 直接当 TEntryPoint；
/// 测试程序集自带一个 public partial Program 占位类型，重写 CreateHost 直接调 PmmHost.CreateApp，
/// 并通过 webHostOverride 注入 .UseTestServer() 让 Mvc.Testing 拿到正确 IServer。
/// </remarks>
public sealed class PmmHostFactory : WebApplicationFactory<Program>
{
    // 默认随机临时 Root（每实例独立）。注意：xUnit IClassFixture 要求夹具类型「有且仅有一个公共构造」，
    // 故只保留无参构造；跨 Host 复用 Root 的需求走 UseFixedRoot() 方法而非构造重载。
    private string _root = Path.Combine(Path.GetTempPath(), $"pmm-test-{Guid.NewGuid():N}");
    private bool _ownsRoot = true;

    /// <summary>固定 Root 以跨多个 Host 复用（模拟「重启」换库）；须在首次 CreateClient/CreateHost 之前调用，清理由调用方负责</summary>
    public PmmHostFactory UseFixedRoot(string root)
    {
        _root = root;
        _ownsRoot = false;
        return this;
    }

    /// <summary>测试夹具暴露的 service 覆盖钩子（E2E 场景替换 ITmdbSearchService / IAiCallOrchestrator 等）</summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; set; }

    /// <summary>暴露 AppPaths 让测试可以读 TargetRoot / LogDir 等</summary>
    public AppPaths Paths { get; private set; } = null!;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // 不复用基类的 HostBuilder（PmmHost.CreateApp 自带 WebApplicationBuilder）
        Paths = AppPaths.ForRoot(_root);
        WebApplication app = PmmHost.CreateApp(
            args: [],
            paths: Paths,
            webHostOverride: wh => wh.UseTestServer(),
            servicesOverride: ConfigureTestServices);

        // 应用 Migration 建表 + 写入种子数据（生产由 Launcher.Program 跑；测试夹具同步跑一次）
        using (IServiceScope scope = app.Services.CreateScope())
        {
            IDbContextFactory<PmmDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
            using PmmDbContext ctx = factory.CreateDbContext();
            ctx.Database.Migrate();
        }

        app.Start();
        return app;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsRoot && Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* 测试目录清理失败不阻断；下次启动覆盖 */ }
        }
    }
}

/// <summary>WebApplicationFactory&lt;TEntryPoint&gt; 占位类型；不承载任何启动逻辑</summary>
public sealed partial class Program;
