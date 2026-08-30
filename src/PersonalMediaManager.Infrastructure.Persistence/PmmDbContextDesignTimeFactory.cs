using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PersonalMediaManager.Infrastructure.Persistence;

/// <summary>EF Core 设计时工厂：供 dotnet ef migrations 工具创建 PmmDbContext</summary>
/// <remarks>
/// 生产环境不使用本工厂；运行时由 AddInfrastructurePersistence 注入的 IDbContextFactory&lt;PmmDbContext&gt; 负责创建。
/// 设计时仅生成 Migrations，连接字符串无意义，固定指向当前目录 pmm-design.db。
/// 使用命令：
///   dotnet ef migrations add Init -p src/PersonalMediaManager.Infrastructure.Persistence -s src/PersonalMediaManager.Infrastructure.Persistence
/// </remarks>
public sealed class PmmDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PmmDbContext>
{
    public PmmDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PmmDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=pmm-design.db");
        return new PmmDbContext(optionsBuilder.Options);
    }
}
