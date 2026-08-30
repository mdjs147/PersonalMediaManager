using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.WatchDirectories;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Watch_Folder 表配置（聚合根含 RowVersion）</summary>
internal sealed class WatchFolderConfig : IEntityTypeConfiguration<WatchFolder>
{
    public void Configure(EntityTypeBuilder<WatchFolder> b)
    {
        b.ToTable("Watch_Folder");
        b.HasKey(x => x.Id);

        b.Property(x => x.Path).HasMaxLength(500).IsRequired();
        b.Property(x => x.Alias).HasMaxLength(64);
        b.Property(x => x.IsTransit).HasDefaultValue(false).IsRequired();
        b.Property(x => x.IsNetworkShare).HasDefaultValue(false).IsRequired();
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();
        // 默认 true：存量目录升级后保持原有「自动监控」行为不变
        b.Property(x => x.AutoScan).HasDefaultValue(true).IsRequired();
        b.Property(x => x.Priority).HasDefaultValue(100).IsRequired();
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasIndex(x => x.Path).IsUnique().HasDatabaseName("UQ_Watch_Folder_Path");
        b.HasIndex(x => new { x.Enabled, x.IsNetworkShare }).HasDatabaseName("IX_Watch_Folder_Enabled_IsNetworkShare");

        b.Ignore(x => x.DomainEvents);
    }
}
