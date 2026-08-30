using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Infrastructure.Persistence.Conversions;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Webhook_Subscription 表配置（聚合根 + Events JSON 列）</summary>
internal sealed class WebhookSubscriptionConfig : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> b)
    {
        b.ToTable("Webhook_Subscription");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(64).IsRequired();
        b.Property(x => x.Url).HasMaxLength(500).IsRequired();
        b.Property(x => x.SecretEncrypted).HasMaxLength(4000);
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();
        b.Property(x => x.TimeoutSeconds).HasDefaultValue(10).IsRequired();
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        // Events List<string>（非空）→ JSON TEXT 列，使用 NonNull 版避免 CS8620 nullable 注解差异
        b.Property(x => x.Events)
         .HasMaxLength(500)
         .HasDefaultValue(new List<string>())
         .HasConversion(new JsonValueConverterNonNull<List<string>>())
         .Metadata.SetValueComparer(new JsonValueComparerNonNull<List<string>>());

        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UQ_Webhook_Subscription_Name");
        b.HasIndex(x => x.Enabled).HasDatabaseName("IX_Webhook_Subscription_Enabled");

        b.Ignore(x => x.DomainEvents);
    }
}
