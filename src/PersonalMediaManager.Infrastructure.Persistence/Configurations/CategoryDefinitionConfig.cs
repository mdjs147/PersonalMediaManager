using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Category_Definition 表配置</summary>
internal sealed class CategoryDefinitionConfig : IEntityTypeConfiguration<CategoryDefinition>
{
    public void Configure(EntityTypeBuilder<CategoryDefinition> b)
    {
        b.ToTable("Category_Definition");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(64).IsRequired();
        b.Property(x => x.MediaType).HasMaxLength(16).HasConversion<string>().IsRequired();
        b.Property(x => x.TargetRoot).HasMaxLength(500).IsRequired();
        b.Property(x => x.Priority).HasDefaultValue(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UQ_Category_Definition_Name");
        b.HasIndex(x => x.MediaType).HasDatabaseName("IX_Category_Definition_MediaType");
    }
}
