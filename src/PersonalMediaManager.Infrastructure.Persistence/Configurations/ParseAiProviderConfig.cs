using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Parse_AiProvider 表配置（聚合根）</summary>
/// <remarks>
/// 反向关联（FK 反向可见性，P1-r3.2 补足）：
/// - Audit_AiCall (1:N, ON DELETE CASCADE)：FK ProviderId
///
/// EF Core 模型注册策略：当聚合根**无** navigation property 指向 Audit_AiCall 时，
/// 关系完整定义只在子端 (<see cref="AuditAiCallConfig"/> Configure 方法 HasOne&lt;ParseAiProvider&gt;().WithMany()) 声明。
/// 父端**不重复**调用 HasMany 反向声明，避免 EF 把双端 anonymous unidirectional 误识别为两个独立关系
/// （会同时生成两个 FK 列 + 重复约束名冲突）。本注释承担「父端文档化关联」职责。
/// </remarks>
internal sealed class ParseAiProviderConfig : IEntityTypeConfiguration<ParseAiProvider>
{
    public void Configure(EntityTypeBuilder<ParseAiProvider> b)
    {
        b.ToTable("Parse_AiProvider");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(64).IsRequired();
        b.Property(x => x.Type).HasMaxLength(32).HasConversion<string>().IsRequired();
        // 成本档位（字符串存储 "Paid"/"Free"）：独立于协议的正交维度，默认 Paid（老库迁移后保持付费档行为）
        b.Property(x => x.CostTier).HasMaxLength(16).HasConversion<string>().HasDefaultValue(AiCostTier.Paid).IsRequired();
        // 是否支持结构化 JSON 输出：默认 true（保持现有 response_format 行为）
        b.Property(x => x.StructuredJson).HasDefaultValue(true).IsRequired();
        b.Property(x => x.BaseUrl).HasMaxLength(500).IsRequired();
        b.Property(x => x.ApiKeyEncrypted).HasMaxLength(4000);
        b.Property(x => x.Model).HasMaxLength(100).IsRequired();
        b.Property(x => x.IsPrimary).HasDefaultValue(false).IsRequired();
        b.Property(x => x.Priority).HasDefaultValue(100).IsRequired();
        // 满意度阈值（可空 REAL）：本 provider 返回置信度低于此值 → 升级到下一级；null = 回退全局 Parse.AiConfidenceThreshold
        b.Property(x => x.ConfidenceThreshold);
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();
        b.Property(x => x.TimeoutSeconds).HasDefaultValue(30).IsRequired();
        // 是否走代理（新增字段，老库迁移后默认 false，保持现网行为不变）
        b.Property(x => x.UseProxy).HasDefaultValue(false).IsRequired();
        // ExtraOptions 列：CLR 类型已是 string?（JSON 扩展参数 temperature/topP 等，AiProvider 实现自行解析），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，确保调参写入能被 ChangeTracking 捕获。
        b.Property(x => x.ExtraOptions)
         .HasMaxLength(2000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));
        // 套餐配额：三个上限可空（null = 不限）；两个 Used 计数器默认 0（老库迁移后从零累计，行数极少无需新索引）
        b.Property(x => x.QuotaUsedCalls).HasDefaultValue(0L).IsRequired();
        b.Property(x => x.QuotaUsedTokens).HasDefaultValue(0L).IsRequired();
        // 周期滚动额度（正交于终身配额）：QuotaPeriod 字符串存储（"None"/"Daily"/"Weekly"/"Monthly"），默认 None（老库迁移后不启用周期额度）；
        // 两个周期 Used 计数器默认 0；限额与 ResetAt 可空走约定（同 QuotaCallLimit 等）
        b.Property(x => x.QuotaPeriod).HasMaxLength(16).HasConversion<string>().HasDefaultValue(AiQuotaPeriod.None).IsRequired();
        b.Property(x => x.QuotaPeriodTimeZone).HasMaxLength(64);
        b.Property(x => x.QuotaPeriodUsedCalls).HasDefaultValue(0L).IsRequired();
        b.Property(x => x.QuotaPeriodUsedTokens).HasDefaultValue(0L).IsRequired();
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UQ_Parse_AiProvider_Name");
        b.HasIndex(x => new { x.Enabled, x.Priority }).HasDatabaseName("IX_Parse_AiProvider_Enabled_Priority");
        b.HasIndex(x => x.IsPrimary).HasDatabaseName("IX_Parse_AiProvider_IsPrimary");
        b.HasIndex(x => x.DisabledUntil).HasDatabaseName("IX_Parse_AiProvider_DisabledUntil");

        b.Ignore(x => x.DomainEvents);
    }
}
