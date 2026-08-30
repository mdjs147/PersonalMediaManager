using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_WorkGenre 连接表配置（复合 PK，FK 作品 CASCADE / FK 类型 RESTRICT）</summary>
internal sealed class MediaWorkGenreConfig : IEntityTypeConfiguration<MediaWorkGenre>
{
    public void Configure(EntityTypeBuilder<MediaWorkGenre> b)
    {
        b.ToTable("Media_WorkGenre");
        b.HasKey(x => new { x.WorkId, x.GenreId });

        b.HasOne<MediaWork>()
         .WithMany(w => w.Genres)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_WorkGenre_Media_Work_WorkId");

        b.HasOne<MediaGenre>()
         .WithMany()
         .HasForeignKey(x => x.GenreId)
         .OnDelete(DeleteBehavior.Restrict)
         .HasConstraintName("FK_Media_WorkGenre_Media_Genre_GenreId");

        // 反查：按类型检索全库作品
        b.HasIndex(x => x.GenreId).HasDatabaseName("IX_Media_WorkGenre_GenreId");
    }
}
