using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_Episode 表配置（剧集单集 + 每集简介，FK 作品 CASCADE + 唯一季集）</summary>
internal sealed class MediaEpisodeConfig : IEntityTypeConfiguration<MediaEpisode>
{
    public void Configure(EntityTypeBuilder<MediaEpisode> b)
    {
        b.ToTable("Media_Episode");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(500);
        b.Property(x => x.Overview).HasMaxLength(4000);
        b.Property(x => x.StillPath).HasMaxLength(300);

        b.HasOne<MediaWork>()
         .WithMany(w => w.Episodes)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_Episode_Media_Work_WorkId");

        b.HasIndex(x => new { x.WorkId, x.SeasonNumber, x.EpisodeNumber })
         .IsUnique().HasDatabaseName("UQ_Media_Episode_WorkId_Season_Episode");
    }
}
