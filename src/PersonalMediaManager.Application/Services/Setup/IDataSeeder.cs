namespace PersonalMediaManager.Application.Services.Setup;

/// <summary>初始种子数据补齐契约（数据库设计 §3.10）</summary>
/// <remarks>
/// 实现放 Infrastructure.Persistence/Services/Setup/DataSeeder（§0.5 Application 仅 Microsoft.Extensions.*）。
/// 仅由 Launcher 在 Migrate 之后调用一次：按「整表为空」判断幂等，已有数据则跳过，绝不覆盖用户配置。
/// 负责补齐 HasData 不便处理的关联数据：媒体分类 + 分类匹配规则 + 解析规则。
/// 不在 EF 模型（HasData）里注入，是为了避免污染 EnsureCreated 建库的测试夹具与分类相关测试语义。
/// </remarks>
public interface IDataSeeder
{
    /// <summary>幂等补齐种子数据；整表已有数据时跳过</summary>
    Task SeedAsync(CancellationToken ct = default);
}
