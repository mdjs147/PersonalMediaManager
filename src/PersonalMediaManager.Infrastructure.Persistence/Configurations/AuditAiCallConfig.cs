using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Audit_AiCall 表配置（FK ProviderId CASCADE + FK MediaItemId SET NULL + 复合统计索引）</summary>
internal sealed class AuditAiCallConfig : IEntityTypeConfiguration<AuditAiCall>
{
    public void Configure(EntityTypeBuilder<AuditAiCall> b)
    {
        b.ToTable("Audit_AiCall");
        b.HasKey(x => x.Id);

        b.Property(x => x.Success).HasDefaultValue(false).IsRequired();
        b.Property(x => x.LatencyMs).HasDefaultValue(0).IsRequired();
        b.Property(x => x.ErrorType).HasMaxLength(64);
        b.Property(x => x.ErrorDetail).HasMaxLength(1000);
        b.Property(x => x.Timestamp).IsRequired();

        // 监控增强（0.13.0）新列：均可空/带默认，对历史行向后兼容
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.ChainId).HasMaxLength(64);
        b.Property(x => x.AttemptLevel).HasDefaultValue(0).IsRequired();
        b.Property(x => x.IsPrimary).HasDefaultValue(false).IsRequired();
        // RequestText / ResponseText：原文 TEXT 无 DB 长度上限，写入端按安全阀截断（防异常巨包）
        // PromptTokens / CompletionTokens / Confidence / HttpStatus：可空值类型，EF 自动映射无需显式配置

        b.HasOne<ParseAiProvider>()
         .WithMany()
         .HasForeignKey(x => x.ProviderId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Audit_AiCall_Parse_AiProvider_ProviderId");

        b.HasOne<MediaItem>()
         .WithMany()
         .HasForeignKey(x => x.MediaItemId)
         .OnDelete(DeleteBehavior.SetNull)
         .HasConstraintName("FK_Audit_AiCall_Media_Item_MediaItemId");

        b.HasIndex(x => new { x.ProviderId, x.Timestamp }).HasDatabaseName("IX_Audit_AiCall_ProviderId_Timestamp");
        b.HasIndex(x => new { x.ProviderId, x.Success, x.Timestamp }).HasDatabaseName("IX_Audit_AiCall_ProviderId_Success_Timestamp");
        // 升级链按 ChainId 聚合（监控页「升级链还原」+ 按链筛选日志）
        b.HasIndex(x => x.ChainId).HasDatabaseName("IX_Audit_AiCall_ChainId");
        // 注：按媒体项查 AI 调用 / 删 MediaItem 时 SET NULL 所需的 IX_Audit_AiCall_MediaItemId 索引，
        // 已由 Init migration 随 FK 约定自动建立、迄今无 migration 删除；EF 运行时仍按 FK 约定补全，
        // 故无需在此显式 HasIndex（显式声明与既有索引等价，只会生成空 migration）。
    }
}
