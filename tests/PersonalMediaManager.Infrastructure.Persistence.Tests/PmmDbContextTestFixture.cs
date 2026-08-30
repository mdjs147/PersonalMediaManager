using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.Interceptors;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>SQLite in-memory + EnsureCreated + 双拦截器接入的最小 DbContext 工厂</summary>
/// <remarks>
/// 用 in-memory SQLite（DataSource=:memory:; Mode=Memory; Cache=Shared）让测试无需文件 IO；
/// 共享同一 SqliteConnection 保活 DbContext 销毁后内存库即丢失，所以 fixture 持有连接生命周期。
/// 拦截器手工 new + 直接 AddInterceptors，避免拉起整套 ServiceProvider。
/// </remarks>
public sealed class PmmDbContextTestFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public PmmDbContextTestFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using PmmDbContext seedCtx = CreateContext();
        seedCtx.Database.EnsureCreated();
    }

    public PmmDbContext CreateContext(Func<DateTimeOffset>? nowProvider = null)
    {
        DbContextOptionsBuilder<PmmDbContext> opts = new();
        opts.UseSqlite(_connection);
        opts.AddInterceptors(
            new TimestampInterceptor(nowProvider ?? (() => DateTimeOffset.UtcNow)),
            new RowVersionInterceptor());
        return new PmmDbContext(opts.Options);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
