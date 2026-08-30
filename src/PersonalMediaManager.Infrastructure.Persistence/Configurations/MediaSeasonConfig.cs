using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Season 表配置（剧集季摘要，FK 作品 CASCADE + 唯一季号）</summary>
internal sealed class MediaSeasonConfig : IEntityTypeConfiguration<MediaSeason>
{
    public void Configure(EntityTypeBuilder<MediaSeason> b)
    {
        b.ToTable("Media_Season");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(300);
        b.Property(x => x.Overview).HasMaxLength(4000);
        b.Property(x => x.PosterPath).HasMaxLength(300);

        b.HasOne<MediaWork>()
         .WithMany(w => w.Seasons)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_Season_Media_Work_WorkId");

        b.HasIndex(x => new { x.WorkId, x.SeasonNumber }).IsUnique().HasDatabaseName("UQ_Media_Season_WorkId_SeasonNumber");
    }
}
