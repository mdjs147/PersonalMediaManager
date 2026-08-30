using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Subtitle_Download 表配置（MediaItem 子表 + CASCADE 级联 + 时间索引）</summary>
internal sealed class SubtitleDownloadConfig : IEntityTypeConfiguration<SubtitleDownload>
{
    public void Configure(EntityTypeBuilder<SubtitleDownload> b)
    {
        b.ToTable("Subtitle_Download");
        b.HasKey(x => x.Id);

        b.Property(x => x.Provider).HasMaxLength(32).HasConversion<string>().IsRequired();
        b.Property(x => x.SubtitleName).HasMaxLength(500);
        b.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.TargetPath).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Language).HasMaxLength(16).IsRequired();

        // CASCADE 关联 Media_Item：MediaItem 无反向导航，关系只在子端声明（照 ProcessStepConfig 惯例）
        b.HasOne<MediaItem>()
         .WithMany()
         .HasForeignKey(x => x.MediaItemId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Subtitle_Download_Media_Item_MediaItemId");

        // 主索引：按 MediaItem + 时间拉取字幕下载记录
        b.HasIndex(x => new { x.MediaItemId, x.CreatedAt }).HasDatabaseName("IX_Subtitle_Download_MediaItemId_CreatedAt");
    }
}
