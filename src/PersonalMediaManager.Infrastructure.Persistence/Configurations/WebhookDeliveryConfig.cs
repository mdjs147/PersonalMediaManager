using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Webhook_Delivery 表配置（FK CASCADE）</summary>
internal sealed class WebhookDeliveryConfig : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("Webhook_Delivery");
        b.HasKey(x => x.Id);

        b.Property(x => x.Event).HasMaxLength(64).IsRequired();
        // Payload 列：CLR 类型已是 string（JSON 请求体原文，Worker 重投时整体覆盖），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，确保重投改写时 ChangeTracking 能正确识别。
        b.Property(x => x.Payload)
         .IsRequired()
         .Metadata.SetValueComparer(new ValueComparer<string>(
             (a, b) => a == b,
             v => v.GetHashCode(),
             v => v));
        b.Property(x => x.Status).HasMaxLength(16).HasConversion<string>().IsRequired();
        b.Property(x => x.Attempts).HasDefaultValue(0).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.Property(x => x.RequestId).HasMaxLength(64).IsRequired();

        b.HasOne<WebhookSubscription>()
         .WithMany()
         .HasForeignKey(x => x.SubscriptionId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Webhook_Delivery_Webhook_Subscription_SubscriptionId");

        b.HasIndex(x => new { x.SubscriptionId, x.Status, x.LastTriedAt })
         .HasDatabaseName("IX_Webhook_Delivery_SubscriptionId_Status_LastTriedAt");
        b.HasIndex(x => x.Event).HasDatabaseName("IX_Webhook_Delivery_Event");
        b.HasIndex(x => x.RequestId).HasDatabaseName("IX_Webhook_Delivery_RequestId");
        // r2 P3-r2.1：重试扫描热路径是 Status IN ('Pending','Retrying') AND NextRetryAt <= now，
        // 单列 NextRetryAt 选择性低（成功/失败行 NextRetryAt=NULL 占多数）；复合索引让 WebhookRetryCoordinator 扫描更聚焦
        // r3 P2-r3.3：删除冗余单列 IX_Webhook_Delivery_NextRetryAt —— 全仓查询只走
        // WebhookRetryCoordinator.SweepAsync（Where Status IN AND NextRetryAt <= now，OrderBy Id），
        // 复合索引 (Status, NextRetryAt) 已完全覆盖；单列索引无任何查询路径使用，纯占空间增写放大
        b.HasIndex(x => new { x.Status, x.NextRetryAt })
         .HasDatabaseName("IX_Webhook_Delivery_Status_NextRetryAt");
    }
}
