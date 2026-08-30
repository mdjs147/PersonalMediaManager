using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.ParseRules;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Parse_Rule 表配置（聚合根）</summary>
internal sealed class ParseRuleConfig : IEntityTypeConfiguration<ParseRule>
{
    public void Configure(EntityTypeBuilder<ParseRule> b)
    {
        b.ToTable("Parse_Rule");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Scope).HasMaxLength(16).HasConversion<string>().HasDefaultValue(ParseScope.FileName).HasSentinel(ParseScope.FileName).IsRequired();
        b.Property(x => x.Pattern).HasMaxLength(1000).IsRequired();
        b.Property(x => x.DefaultType).HasMaxLength(8);
        b.Property(x => x.ForceType).HasDefaultValue(false).IsRequired();
        b.Property(x => x.Priority).HasDefaultValue(100).IsRequired();
        b.Property(x => x.ConfidenceBonus).HasDefaultValue(0.0).IsRequired();
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UQ_Parse_Rule_Name");
        b.HasIndex(x => new { x.Enabled, x.Priority }).HasDatabaseName("IX_Parse_Rule_Enabled_Priority");

        b.Ignore(x => x.DomainEvents);
    }
}
