using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Process_Step 表配置（MediaItem 子表 + CASCADE 级联 + 时间索引）</summary>
internal sealed class ProcessStepConfig : IEntityTypeConfiguration<ProcessStep>
{
    public void Configure(EntityTypeBuilder<ProcessStep> b)
    {
        b.ToTable("Process_Step");
        b.HasKey(x => x.Id);

        b.Property(x => x.Stage).HasConversion<int>().IsRequired();
        b.Property(x => x.StartedAt).IsRequired();
        b.Property(x => x.DurMs).HasDefaultValue(0L).IsRequired();
        // r3 P3-r3.6：Detail 故意不加 IsRequired() —— Domain 层 ProcessStep.Detail 是 string?（见 ProcessStep.cs:43），
        // Detected / Queued 等无需 detail 的步骤合法传 null；EF 由 CLR 可空性自动推导成 nullable TEXT 列，符合业务语义
        // 单步详情上限 8000 字符，超长截断（AI prompt/output 可能较大）
        b.Property(x => x.Detail).HasMaxLength(8000);

        b.HasOne<MediaItem>()
         .WithMany(m => m.Steps)
         .HasForeignKey(x => x.MediaItemId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Process_Step_Media_Item_MediaItemId");

        // 主索引：按 MediaItem + 时间拉取详情时间线
        b.HasIndex(x => new { x.MediaItemId, x.StartedAt }).HasDatabaseName("IX_Process_Step_MediaItemId_StartedAt");
    }
}
