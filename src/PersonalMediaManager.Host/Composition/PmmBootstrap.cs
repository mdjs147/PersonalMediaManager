using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Composition;

/// <summary>共享迁移、自愈、种子和宿主启动顺序</summary>
public static class PmmBootstrap
{
    public static async Task StartAsync(WebApplication app, CancellationToken ct = default)
    {
        // CreateApp 已处理待导入快照；任何后台任务启动前完成迁移和幂等种子。
        if (!OperatingSystem.IsWindows())
        {
            string database = app.Services.GetRequiredService<DatabaseFileLocation>().Path;
            if (File.Exists(database)) PrivateFileSystem.RestrictFile(database);
            else
            {
                using FileStream file = PrivateFileSystem.CreateNew(database);
            }
        }
        using (IServiceScope scope = app.Services.CreateScope())
        {
            IDbContextFactory<PmmDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
            await using PmmDbContext ctx = await factory.CreateDbContextAsync(ct);
            ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("PersonalMediaManager.Host.DbStartup");
            ct.ThrowIfCancellationRequested();
            DatabaseStartupHealer.MigrateWithSelfHeal(ctx, logger);
            await scope.ServiceProvider.GetRequiredService<IDataSeeder>().SeedAsync(ct);
        }
        await app.StartAsync(ct);
    }
}
