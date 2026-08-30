using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Aggregates.MediaWorks;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>Media_WorkKeyword 连接表配置（复合 PK，FK 作品 CASCADE / FK 关键词 RESTRICT）</summary>
internal sealed class MediaWorkKeywordConfig : IEntityTypeConfiguration<MediaWorkKeyword>
{
    public void Configure(EntityTypeBuilder<MediaWorkKeyword> b)
    {
        b.ToTable("Media_WorkKeyword");
        b.HasKey(x => new { x.WorkId, x.KeywordId });

        b.HasOne<MediaWork>()
         .WithMany(w => w.Keywords)
         .HasForeignKey(x => x.WorkId)
         .OnDelete(DeleteBehavior.Cascade)
         .HasConstraintName("FK_Media_WorkKeyword_Media_Work_WorkId");

        b.HasOne<MediaKeyword>()
         .WithMany()
         .HasForeignKey(x => x.KeywordId)
         .OnDelete(DeleteBehavior.Restrict)
         .HasConstraintName("FK_Media_WorkKeyword_Media_Keyword_KeywordId");

        b.HasIndex(x => x.KeywordId).HasDatabaseName("IX_Media_WorkKeyword_KeywordId");
    }
}
