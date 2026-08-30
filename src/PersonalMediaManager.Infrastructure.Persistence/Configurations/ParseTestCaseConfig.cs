using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.ParseTestCases;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Parse_TestCase 表配置（聚合根）</summary>
/// <remarks>
/// JSON 列（LastRunResult / AiVerdict）CLR 类型为 string?，Domain 不解析；
/// ValueComparer 显式按字符串值比较，避免 EF 默认引用比较带来的脏检查误判。
/// </remarks>
internal sealed class ParseTestCaseConfig : IEntityTypeConfiguration<ParseTestCase>
{
    public void Configure(EntityTypeBuilder<ParseTestCase> b)
    {
        b.ToTable("Parse_TestCase");
        b.HasKey(x => x.Id);

        b.Property(x => x.Source)
         .HasMaxLength(16)
         .HasConversion<string>()
         .HasDefaultValue(ParseTestCaseSource.Manual)
         .HasSentinel(ParseTestCaseSource.Manual)
         .IsRequired();

        b.Property(x => x.Status)
         .HasMaxLength(16)
         .HasConversion<string>()
         .HasDefaultValue(ParseTestCaseStatus.PendingTriage)
         .HasSentinel(ParseTestCaseStatus.PendingTriage)
         .IsRequired();

        b.Property(x => x.SamplePath).HasMaxLength(1000).IsRequired();
        b.Property(x => x.WatchRootPath).HasMaxLength(1000);

        b.Property(x => x.ExpectedTitle).HasMaxLength(300);
        b.Property(x => x.ExpectedYear);
        b.Property(x => x.ExpectedMediaType)
         .HasMaxLength(8)
         .HasConversion<string>();
        b.Property(x => x.ExpectedSeason);
        b.Property(x => x.ExpectedEpisode);

        b.Property(x => x.LastRunAt);
        b.Property(x => x.LastRunStatus)
         .HasMaxLength(16)
         .HasConversion<string>()
         .HasDefaultValue(ParseTestCaseRunStatus.NotRun)
         .HasSentinel(ParseTestCaseRunStatus.NotRun)
         .IsRequired();

        // LastRunResult / AiVerdict 为 JSON 字符串列：显式 ValueComparer 字符串值比较
        b.Property(x => x.LastRunResult)
         .HasMaxLength(2000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));

        b.Property(x => x.LastMatchedRuleId);

        b.Property(x => x.AiVerdict)
         .HasMaxLength(2000)
         .Metadata.SetValueComparer(new ValueComparer<string?>(
             (a, b) => a == b,
             v => v == null ? 0 : v.GetHashCode(),
             v => v));

        b.Property(x => x.AiSuggestedRulePattern).HasMaxLength(1000);
        b.Property(x => x.Notes).HasMaxLength(500);

        b.Property(x => x.RowVersion).IsConcurrencyToken().HasDefaultValue(0L).IsRequired();

        // SamplePath 唯一：相同样本不允许重复入库
        b.HasIndex(x => x.SamplePath).IsUnique().HasDatabaseName("UQ_Parse_TestCase_SamplePath");
        // 按状态过滤列表（暂存区 / 正式样本切换）走索引
        b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_Parse_TestCase_Status_CreatedAt");
        // 命中规则反查：被某条规则覆盖的样本列表
        b.HasIndex(x => x.LastMatchedRuleId).HasDatabaseName("IX_Parse_TestCase_LastMatchedRuleId");

        b.Ignore(x => x.DomainEvents);
    }
}
