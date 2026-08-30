using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Category_MatchRule 表配置（FK CategoryId CASCADE）</summary>
internal sealed class CategoryMatchRuleConfig : IEntityTypeConfiguration<CategoryMatchRule>
{
    public void Configure(EntityTypeBuilder<CategoryMatchRule> b)
    {
        b.ToTable("Category_MatchRule");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(100);
        // Conditions 列：CLR 类型已是 string（JSON 条件树原文，Application 层解析 all/any + eq/in/contains），
        // 不需要 Converter；显式 ValueComparer<string> 锁定按值比较，符合 JSON 列契约。
        b.Property(x => x.Conditions)
         .HasMaxLength(4000)
         .HasDefaultValue("{}")
         .IsRequired()
         .Metadata.SetValueComparer(new ValueComparer<string>(
             (a, b) => a == b,
             v => v.GetHashCode(),
             v => v));
        b.Property(x => x.Priority).HasDefaultValue(100).IsRequired();
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasOne<CategoryDefinition>()
         .WithMany()
         .HasForeignKey(x => x.CategoryId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Category_MatchRule_Category_Definition_CategoryId");

        b.HasIndex(x => x.CategoryId).HasDatabaseName("IX_Category_MatchRule_CategoryId");
        b.HasIndex(x => new { x.Enabled, x.Priority }).HasDatabaseName("IX_Category_MatchRule_Enabled_Priority");
    }
}
